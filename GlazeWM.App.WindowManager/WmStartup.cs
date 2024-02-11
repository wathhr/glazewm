using System.Reactive.Linq;
using GlazeWM.Domain.Common.Commands;
using GlazeWM.Domain.Common.Enums;
using GlazeWM.Domain.Containers;
using GlazeWM.Domain.Containers.Commands;
using GlazeWM.Domain.Containers.Events;
using GlazeWM.Domain.UserConfigs;
using GlazeWM.Domain.UserConfigs.Commands;
using GlazeWM.Domain.Windows;
using GlazeWM.Domain.Windows.Commands;
using GlazeWM.Infrastructure.Bussing;
using GlazeWM.Infrastructure.Common;
using GlazeWM.Infrastructure.Common.Commands;
using GlazeWM.Infrastructure.Common.Events;
using GlazeWM.Infrastructure.WindowsApi;
using Windows.ApplicationModel.Background;
using static GlazeWM.Infrastructure.WindowsApi.WindowsApiService;

namespace GlazeWM.App.WindowManager
{
  public sealed class WmStartup
  {
    private readonly Bus _bus;
    private readonly KeybindingService _keybindingService;
    private readonly WindowService _windowService;
    private readonly WindowEventService _windowEventService;
    private readonly WindowService _windowService;
    private readonly UserConfigService _userConfigService;

    private SystemTrayIcon? _systemTrayIcon { get; set; }

    public WmStartup(
      Bus bus,
      KeybindingService keybindingService,
      WindowService windowService,
      WindowEventService windowEventService,
      WindowService windowService,
      UserConfigService userConfigService)
    {
      _bus = bus;
      _keybindingService = keybindingService;
      _windowService = windowService;
      _windowEventService = windowEventService;
      _windowService = windowService;
      _userConfigService = userConfigService;
    }

    public ExitCode Run()
    {
      try
      {
        // Set the process-default DPI awareness.
        _ = SetProcessDpiAwarenessContext(DpiAwarenessContext.PerMonitorAwareV2);

        _bus.Events.OfType<ApplicationExitingEvent>()
          .Subscribe(_ => OnApplicationExit());

        // Populate initial monitors, windows, workspaces and user config.
        _bus.Invoke(new PopulateInitialStateCommand());
        _bus.Invoke(new RedrawContainersCommand());
        _bus.Invoke(new SyncNativeFocusCommand());

        // Listen on registered keybindings.
        _keybindingService.Start();

        // Listen for window events (eg. close, focus).
        _windowEventService.Start();

        // Listen for changes to display settings.
        // TODO: Unsubscribe on application exit.
        SystemEvents.DisplaySettingsChanged.Subscribe((@event) => _bus.EmitAsync(@event));

        var systemTrayIconConfig = new SystemTrayIconConfig
        {
          HoverText = "GlazeWM",
          IconResourceName = "GlazeWM.App.Resources.icon.ico",
          Actions = new Dictionary<string, Action>
          {
            { "Reload config", () => _bus.Invoke(new ReloadUserConfigCommand()) },
            { "Exit", () => _bus.Invoke(new ExitApplicationCommand(false)) },
          }
        };

        // Add application to system tray.
        _systemTrayIcon = new SystemTrayIcon(systemTrayIconConfig);
        _systemTrayIcon.Show();

        var windowAnimations = _userConfigService.GeneralConfig.WindowAnimations;

        // Enable/disable window transition animations.
        if (windowAnimations is not WindowAnimations.Unchanged)
        {
          SystemSettings.SetWindowAnimationsEnabled(
            windowAnimations is WindowAnimations.True
          );
        }

        if (_userConfigService.FocusBorderConfig.Active.Enabled ||
            _userConfigService.FocusBorderConfig.Inactive.Enabled)
        {
          _bus.Events.OfType<FocusChangedEvent>().Subscribe((@event) =>
            _bus.InvokeAsync(
              new SetActiveWindowBorderCommand(@event.FocusedContainer as Window)
            )
          );
        }

        // Hook mouse event for focus follows cursor.
        if (_userConfigService.GeneralConfig.FocusFollowsCursor)
          MouseEvents.MouseMoves.Sample(TimeSpan.FromMilliseconds(50)).Subscribe((@event) =>
          {
            if (!@event.IsLMouseDown && !@event.IsRMouseDown)
              _bus.InvokeAsync(new FocusContainerUnderCursorCommand(@event.Point));
          });

        // Setup cursor follows focus
        if (_userConfigService.GeneralConfig.CursorFollowsFocus)
        {
          var focusedContainerMoved = _bus.Events
            .OfType<FocusedContainerMovedEvent>()
            .Select(@event => @event.FocusedContainer);

          var nativeFocusSynced = _bus.Events
            .OfType<NativeFocusSyncedEvent>()
            .Select((@event) => @event.FocusedContainer);

          focusedContainerMoved.Merge(nativeFocusSynced)
            .Where(container => container is Window)
            .Subscribe((window) => _bus.InvokeAsync(new CenterCursorOnContainerCommand(window)));
        }

        if (_userConfigService.GeneralConfig.AutomaticTilingDirection != AutomaticTilingDirection.Unchanged)
        {
          var focusChanged = _bus.Events
            .OfType<FocusChangedEvent>()
            .Select(@event => @event.FocusedContainer)
            .Where(container => container is Window);

          var windowMovedOrResized = _bus.Events
            .OfType<WindowMovedOrResizedEvent>()
            .Select(@event => _windowService.GetWindowByHandle(@event.WindowHandle));

          var allWatchedEvents = focusChanged.Merge(windowMovedOrResized);
          allWatchedEvents.Subscribe(container => SetAutomaticTilingDirection(container as Window));
        }

        System.Windows.Forms.Application.Run();
        return ExitCode.Success;
      }
      catch (Exception exception)
      {
        _bus.Invoke(new HandleFatalExceptionCommand(exception));
        return ExitCode.Error;
      }
    }

    private void OnApplicationExit()
    {
      // Show all windows regardless of whether their workspace is displayed.
      foreach (var window in _windowService.GetWindows())
        ShowWindowAsync(window.Handle, ShowWindowFlags.ShowNoActivate);

      // Clear border on the active window.
      _bus.Invoke(new SetActiveWindowBorderCommand(null));

      // Destroy the system tray icon.
      _systemTrayIcon?.Remove();

      System.Windows.Forms.Application.Exit();
    }

    private void SetAutomaticTilingDirection(Window window)
    {
      if (window == null)
        return;

      var splitContainer = (window.Parent as SplitContainer);
      var currentTilingDirection = splitContainer.TilingDirection;
      var newTilingDirection = currentTilingDirection;

      switch (_userConfigService.GeneralConfig.AutomaticTilingDirection)
      {
        case AutomaticTilingDirection.Unchanged:
          break;
        case AutomaticTilingDirection.Vertical:
          newTilingDirection = TilingDirection.Vertical;
          break;
        case AutomaticTilingDirection.Horizontal:
          newTilingDirection = TilingDirection.Horizontal;
          break;
        case AutomaticTilingDirection.Alternate:
          // If the window is an only child, it's already in a dedicated SplitContainer
          if (!window.HasSiblings())
          {
            var parentContainer = splitContainer.Parent as SplitContainer;
            if (parentContainer == null || parentContainer.TilingDirection != currentTilingDirection)
            {
              // We've already applied the new direction
              break;
            }
          }
          newTilingDirection = currentTilingDirection == TilingDirection.Horizontal ? TilingDirection.Vertical : TilingDirection.Horizontal;
          break;
        case AutomaticTilingDirection.LargestDimension:
          newTilingDirection = window.Width > window.Height ? TilingDirection.Horizontal : TilingDirection.Vertical;
          break;
      }

      if (newTilingDirection != currentTilingDirection)
      {
        _bus.Invoke(new ChangeTilingDirectionCommand(window, newTilingDirection));
        // Immediately redraw, as changing the tiling direction may have created a new
        // container that gets queued for redraw. Forgetting to do it here might cause
        // glitches during future commands
        _bus.Invoke(new RedrawContainersCommand());
      }
    }
  }
}
