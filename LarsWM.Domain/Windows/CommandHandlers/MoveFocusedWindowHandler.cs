﻿using System.Linq;
using LarsWM.Domain.Common.Enums;
using LarsWM.Domain.Containers;
using LarsWM.Domain.Containers.Commands;
using LarsWM.Domain.Windows.Commands;
using LarsWM.Domain.Workspaces;
using LarsWM.Infrastructure.Bussing;

namespace LarsWM.Domain.Windows.CommandHandlers
{
  class MoveFocusedWindowHandler : ICommandHandler<MoveFocusedWindowCommand>
  {
    private Bus _bus;
    private ContainerService _containerService;
    private WorkspaceService _workspaceService;

    public MoveFocusedWindowHandler(Bus bus, ContainerService containerService, WorkspaceService workspaceService)
    {
      _bus = bus;
      _containerService = containerService;
      _workspaceService = workspaceService;
    }

    public dynamic Handle(MoveFocusedWindowCommand command)
    {
      var focusedWindow = _containerService.FocusedContainer as Window;

      // Ignore cases where focused container is not a window.
      if (focusedWindow == null)
        return CommandResponse.Ok;

      var direction = command.Direction;
      var layoutForDirection = (direction == Direction.LEFT || direction == Direction.RIGHT)
        ? Layout.Horizontal : Layout.Vertical;

      var ancestorWithLayout = focusedWindow.TraverseUpEnumeration()
        .Where(container => (container as SplitContainer)?.Layout == layoutForDirection)
        .FirstOrDefault() as SplitContainer;

      // Change the layout of the workspace to `layoutForDirection`.
      if (ancestorWithLayout == null)
      {
        var workspace = _workspaceService.GetWorkspaceFromChildContainer(focusedWindow);
        workspace.Layout = layoutForDirection;

        _containerService.SplitContainersToRedraw.Add(workspace);
        _bus.Invoke(new RedrawContainersCommand());

        return CommandResponse.Ok;
      }

      // Swap the focused window with sibling in given direction.
      if (ancestorWithLayout == focusedWindow.Parent)
      {
        var index = focusedWindow.SelfAndSiblings.IndexOf(focusedWindow);

        if (direction == Direction.UP || direction == Direction.LEFT)
          _bus.Invoke(new SwapContainersCommand(focusedWindow, focusedWindow.Parent.Children[index - 1]));
        else
          _bus.Invoke(new SwapContainersCommand(focusedWindow, focusedWindow.Parent.Children[index + 1]));

        _bus.Invoke(new RedrawContainersCommand());

        return CommandResponse.Ok;
      }

      // Insert container into `ancestorWithLayout` at start or end.
      // TODO: Should actually traverse up from `focusedWindow` to find container where the parent
      // is `ancestorWithLayout`. Then, depending on direction, insert before or after that container.
      // TODO: Consider changing `InsertPosition` to an int, and set the default to `Siblings.Count - 1`.
      if (direction == Direction.UP || direction == Direction.LEFT)
        _bus.Invoke(new AttachContainerCommand(ancestorWithLayout, focusedWindow));
      else
        _bus.Invoke(new AttachContainerCommand(ancestorWithLayout, focusedWindow, InsertPosition.START));

      _bus.Invoke(new RedrawContainersCommand());

      return CommandResponse.Ok;
    }
  }
}
