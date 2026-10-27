using Content.Shared.Wires;
using Robust.Shared.IoC;
using RobustoClient.Systems.Construction;

namespace RobustoClient.Systems.Hacking;

public sealed partial class RobustaWiresSolver
{
    [Dependency] private readonly RobustaAutoConstructSystem _construction = default!;
    [Dependency] private readonly RobustaConstructionSolver _constructionSolver = default!;

    private void AdvancePanelSecurity(ActiveSolve solve, WiresPanelSecurityComponent security)
    {
        // SecurityLevel is the map-init node; Examine is updated after every actual transition.
        if (solve.RemovingSecurity)
        {
            if (_construction.IsBusy)
            {
                solve.Elapsed = 0f;
                return;
            }

            if (security.Examine == solve.SecurityExamine)
            {
                FailSolve("security removal did not change the replicated security state");
                return;
            }

            solve.RemovingSecurity = false;
            solve.ToolRequested = false;
        }

        if (_construction.IsBusy)
        {
            FailSolve("construction executor is busy");
            return;
        }

        var nodeName = RobustaConstructionSolver.GetSecurityRemovalTarget(security.Examine);
        var nextNode = nodeName != null ? RobustaConstructionSolver.GetNextSecurityNode(nodeName) : null;
        if (nodeName == null || nextNode == null)
        {
            FailSolve($"unsupported panel security state: {security.Examine}");
            return;
        }

        var plan = _constructionSolver.SolvePlanToNode(solve.Target, nextNode);
        if (plan == null || plan.Steps.Count == 0)
        {
            FailSolve("could not solve construction plan for panel security removal");
            return;
        }

        solve.SecurityExamine = security.Examine;
        solve.RemovingSecurity = true;
        solve.ToolRequested = false;
        solve.Elapsed = 0f;
        _construction.StartJob(solve.Target, plan);
    }
}