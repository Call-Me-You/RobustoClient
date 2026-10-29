using Content.Shared.Doors.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Prying.Components;
using Robust.Shared.IoC;

namespace RobustoClient.Systems.Hacking;

public sealed partial class RobustaWiresSolver
{
    [Dependency] private readonly SharedHandsSystem _hands = default!;

    private void AdvanceTargetTool(ActiveSolve solve, SolvedTargetToolAction action)
    {
        if (!TryComp<DoorComponent>(solve.Target, out var door))
        {
            FailSolve("tool target no longer has a door component");
            return;
        }

        // Prying toggles doors; never interact with a door that is already open.
        if (door.State == action.ExpectedState)
        {
            CompleteTargetTool(solve);
            return;
        }

        if (door.State != DoorState.Closed)
            return;

        if (!solve.ToolRequested)
        {
            solve.ToolRequested = _construction.TryEquipToolOrMultiTool(
                action.ToolQuality,
                $"Tool:{action.ToolQuality}");
            if (!solve.ToolRequested)
                FailSolve($"could not equip an enabled {action.ToolQuality} tool");
            return;
        }

        if (_itemUtility.IsBusy)
            return;

        if (!_construction.EnsureHeldToolModeReady(action.ToolQuality, out bool failedTargetToolCycle))
        {
            if (failedTargetToolCycle)
                FailSolve($"could not cycle tool to {action.ToolQuality}");
            else
                solve.Timer = 0.25f;
            return;
        }

        if (_playerManager.LocalEntity is not { } player ||
            _hands.GetActiveItem(player) is not { } held ||
            !_construction.IsToolReadyForQuality(held, action.ToolQuality))
        {
            FailSolve("required tool was not equipped");
            return;
        }

        ClickTarget(solve.Target);
        _phaseRetryTimer = 0f;
        solve.Phase = SolvePhase.WaitForTargetTool;
        solve.Timer = 0.35f;
    }

    private void AdvanceTargetToolVerification(ActiveSolve solve)
    {
        if (solve.Plan?.Actions[solve.ActionIndex] is not SolvedTargetToolAction action ||
            !TryComp<DoorComponent>(solve.Target, out var door))
        {
            FailSolve("lost target tool action or door");
            return;
        }

        // The upstream pry DoAfter and opening animation must finish before reporting success.
        if (door.State == action.ExpectedState)
        {
            CompleteTargetTool(solve);
            return;
        }

        // If the door is still closed and the pry DoAfter didn't start or was interrupted, retry clicking.
        if (door.State == DoorState.Closed && _phaseRetryTimer >= 1.5f)
        {
            _phaseRetryTimer = 0f;
            solve.ToolRequested = false;
            solve.Phase = SolvePhase.ExecuteAction;
            solve.Timer = 0.2f;
        }
    }

    private void CompleteTargetTool(ActiveSolve solve)
    {
        _phaseRetryTimer = 0f;
        solve.ActionIndex++;
        solve.ToolRequested = false;
        solve.Phase = SolvePhase.ExecuteAction;
        solve.Elapsed = 0f;
    }
}