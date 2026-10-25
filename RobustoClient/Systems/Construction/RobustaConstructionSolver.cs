using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Shared.Construction;
using Content.Shared.Construction.Components;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Construction.Steps;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems.Construction;

public enum ConstructionActionKind
{
    StartGhostConstruction,
    AddMaterial,
    UseTool,
    InsertPrototype,
    InsertComponent,
    InsertTag
}

public sealed record ConstructionActionStep(
    ConstructionActionKind Kind,
    string Description,
    string? PrototypeOrQualityOrTag = null,
    int Amount = 1,
    float DoAfterDelay = 0f,
    string? ExpectedNextNode = null
);

public sealed record SolvedConstructionPlan(
    string GraphId,
    string StartNode,
    string TargetNode,
    bool IsDeconstruct,
    List<ConstructionActionStep> Steps
);

public sealed class RobustaConstructionSolver : EntitySystem
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPrototypeManager _protoMan = default!;
    [Dependency] private readonly IComponentFactory _compFactory = default!;
    [Dependency] private readonly ItemUtilitySystem _itemUtil = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;

    private bool _cacheInitialized;

    private readonly Dictionary<string, List<(ConstructionGraphPrototype Graph, ConstructionGraphNode Node)>> _entityToGraphNodes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<ConstructionPrototype>> _graphToConstructionProtos =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _entityToConstructionProto =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] PrimaryDeconstructTargets =
    {
        "start",
        "destroyed",
        "deconstructed",
        "destroyedMachineFrame"
    };

    private static readonly string[] FallbackDeconstructTargets =
    {
        "frame",
        "machineFrame",
        "girder",
        "frameUnsecured",
        "assembly",
        "missingWires"
    };

    private static readonly HashSet<string> NonTerminalBuildNodeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "start",
        "frame",
        "machineFrame",
        "missingWires",
        "computerFrame",
        "frameUnsecured",
        "boardUnsecured",
        "monitorMissing",
        "monitorUnsecured",
        "girder",
        "reinforcedGirder",
        "wallFrame",
        "windowFrame",
        "assembly",
        "wired",
        "electronics",
        "glassElectronics",
        "apcFrame"
    };

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        _cacheInitialized = false;
        _entityToGraphNodes.Clear();
        _graphToConstructionProtos.Clear();
        _entityToConstructionProto.Clear();
    }

    public void EnsureCacheInitialized()
    {
        if (_cacheInitialized)
            return;

        _cacheInitialized = true;
        _entityToGraphNodes.Clear();
        _graphToConstructionProtos.Clear();
        _entityToConstructionProto.Clear();

        foreach (var cProto in _protoMan.EnumeratePrototypes<ConstructionPrototype>())
        {
            var graphId = GetStringMember(cProto, "Graph");
            if (string.IsNullOrEmpty(graphId))
                continue;

            if (!_graphToConstructionProtos.TryGetValue(graphId, out var list))
            {
                list = new List<ConstructionPrototype>();
                _graphToConstructionProtos[graphId] = list;
            }
            list.Add(cProto);

            var targetNodeName = GetStringMember(cProto, "TargetNode");
            if (!string.IsNullOrEmpty(targetNodeName) &&
                _protoMan.TryIndex<ConstructionGraphPrototype>(graphId, out var cGraph) &&
                TryGetNodeByName(cGraph, targetNodeName, out var targetNode))
            {
                var targetEntId = GetNodeEntityId(targetNode, null);
                if (!string.IsNullOrEmpty(targetEntId) && !_entityToConstructionProto.ContainsKey(targetEntId))
                    _entityToConstructionProto[targetEntId] = cProto.ID;
            }
        }

        foreach (var graph in _protoMan.EnumeratePrototypes<ConstructionGraphPrototype>())
        {
            foreach (var node in EnumerateGraphNodes(graph))
            {
                var entityId = GetNodeEntityId(node, null);
                if (!string.IsNullOrEmpty(entityId))
                    RegisterEntityNodeMapping(entityId, graph, node);
            }
        }

        // Map EntityPrototypes that either declare ConstructionComponent in YAML or are constructed from boards
        foreach (var entityProto in _protoMan.EnumeratePrototypes<EntityPrototype>())
        {
            if (TryGetConstructionMappingFromPrototype(entityProto, out var graphId, out var nodeName) &&
                _protoMan.TryIndex<ConstructionGraphPrototype>(graphId, out var graph) &&
                TryGetNodeByName(graph, nodeName, out var node))
            {
                RegisterEntityNodeMapping(entityProto.ID, graph, node);
            }

            if (_itemUtil.TryGetPrototypeComponent<MachineBoardComponent>(entityProto, "MachineBoard", out var machineBoard) &&
                machineBoard != null)
            {
                var machineProtoId = GetStringMember(machineBoard, "Prototype");
                if (!string.IsNullOrEmpty(machineProtoId) &&
                    _protoMan.TryIndex<ConstructionGraphPrototype>("Machine", out var machineGraph) &&
                    TryGetNodeByName(machineGraph, "machine", out var machineNode))
                {
                    RegisterEntityNodeMapping(machineProtoId, machineGraph, machineNode);
                }
            }

            if (_itemUtil.TryGetPrototypeComponent<ComputerBoardComponent>(entityProto, "ComputerBoard", out var compBoard) &&
                compBoard != null)
            {
                var compProtoId = GetStringMember(compBoard, "Prototype");
                if (!string.IsNullOrEmpty(compProtoId) &&
                    _protoMan.TryIndex<ConstructionGraphPrototype>("Computer", out var compGraph) &&
                    TryGetNodeByName(compGraph, "computer", out var compNode))
                {
                    RegisterEntityNodeMapping(compProtoId, compGraph, compNode);
                }
            }

            if (_itemUtil.TryGetPrototypeComponent<ElectronicsBoardComponent>(entityProto, "ElectronicsBoard", out var elecBoard) &&
                elecBoard != null)
            {
                var elecProtoId = GetStringMember(elecBoard, "Prototype");
                if (!string.IsNullOrEmpty(elecProtoId))
                {
                    // Search existing graphs for a BoardNodeEntity node if not already mapped
                    foreach (var g in _protoMan.EnumeratePrototypes<ConstructionGraphPrototype>())
                    {
                        foreach (var n in EnumerateGraphNodes(g))
                        {
                            var entObj = GetMemberValue(n, "Entity");
                            if (entObj != null && entObj.GetType().Name.Contains("BoardNodeEntity", StringComparison.OrdinalIgnoreCase))
                            {
                                RegisterEntityNodeMapping(elecProtoId, g, n);
                            }
                        }
                    }
                }
            }
        }
    }

    private void RegisterEntityNodeMapping(string entityProtoId, ConstructionGraphPrototype graph, ConstructionGraphNode node)
    {
        if (!_entityToGraphNodes.TryGetValue(entityProtoId, out var list))
        {
            list = new List<(ConstructionGraphPrototype, ConstructionGraphNode)>();
            _entityToGraphNodes[entityProtoId] = list;
        }

        foreach (var existing in list)
        {
            if (string.Equals(existing.Graph.ID, graph.ID, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetNodeName(existing.Node), GetNodeName(node), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        list.Add((graph, node));
    }

    private bool TryGetConstructionMappingFromPrototype(EntityPrototype proto, out string graphId, out string nodeName)
    {
        graphId = string.Empty;
        nodeName = string.Empty;

        if (!_itemUtil.TryGetPrototypeComponent<object>(proto, "Construction", out var compObj) || compObj == null)
            return false;

        graphId = GetStringMember(compObj, "Graph") ?? string.Empty;
        nodeName = GetStringMember(compObj, "Node") ?? string.Empty;
        return !string.IsNullOrEmpty(graphId) && !string.IsNullOrEmpty(nodeName);
    }

    public SolvedConstructionPlan? SolveBuildPlan(EntityUid target, string? desiredEntityProtoId = null)
    {
        if (!_entMan.EntityExists(target))
            return null;

        EnsureCacheInitialized();

        // Case A: target has ConstructionGhostComponent
        var ghostComp = FindComponentByName(target, "ConstructionGhostComponent");
        if (ghostComp != null)
        {
            var cProto = GetMemberValue(ghostComp, "Prototype") as ConstructionPrototype;
            if (cProto == null)
            {
                var protoId = GetStringMember(ghostComp, "Prototype");
                if (!string.IsNullOrEmpty(protoId))
                    _protoMan.TryIndex(protoId, out cProto);
            }

            if (cProto != null)
                return SolveBuildPlan(cProto, includeGhostStart: true);
        }

        // Case B: target is an in-world frame/unfinished structure
        if (!TryResolveGraphAndNode(target, out var graph, out var currentNode, out var explicitTargetNode) ||
            graph == null || currentNode == null)
        {
            return null;
        }

        var currentNodeName = GetNodeName(currentNode);
        var targetNodeName = explicitTargetNode;

        if (!string.IsNullOrEmpty(desiredEntityProtoId) &&
            _entityToGraphNodes.TryGetValue(desiredEntityProtoId, out var desiredNodes))
        {
            foreach (var (dGraph, dNode) in desiredNodes)
            {
                if (string.Equals(dGraph.ID, graph.ID, StringComparison.OrdinalIgnoreCase))
                {
                    targetNodeName = GetNodeName(dNode);
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(targetNodeName) ||
            string.Equals(targetNodeName, currentNodeName, StringComparison.OrdinalIgnoreCase))
        {
            targetNodeName = FindBestBuildTargetNode(graph, currentNode);
        }

        if (string.IsNullOrEmpty(targetNodeName) ||
            string.Equals(targetNodeName, currentNodeName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var edgePath = FindShortestEdgePath(graph, currentNodeName, targetNodeName);
        if (edgePath == null || edgePath.Count == 0)
            return null;

        var steps = new List<ConstructionActionStep>();
        foreach (var (fromNode, edge, toNode) in edgePath)
        {
            AppendMachineFrameStepsIfNeeded(target, graph, fromNode, toNode, steps);
            AppendAnchorTransitionStepIfNeeded(graph, fromNode, toNode, isDeconstruct: false, steps);
            ConvertEdgeSteps(edge, GetNodeName(toNode), steps);
        }

        if (steps.Count == 0)
            return null;

        return new SolvedConstructionPlan(
            graph.ID,
            currentNodeName,
            targetNodeName,
            IsDeconstruct: false,
            Steps: steps);
    }

    public SolvedConstructionPlan? SolveBuildPlan(ConstructionPrototype constructionProto, bool includeGhostStart = true)
    {
        EnsureCacheInitialized();

        var graphId = GetStringMember(constructionProto, "Graph");
        var targetNodeName = GetStringMember(constructionProto, "TargetNode");
        var startNodeName = GetStringMember(constructionProto, "StartNode");

        if (string.IsNullOrEmpty(graphId) ||
            string.IsNullOrEmpty(targetNodeName) ||
            !_protoMan.TryIndex<ConstructionGraphPrototype>(graphId, out var graph))
        {
            return null;
        }

        if (string.IsNullOrEmpty(startNodeName))
            startNodeName = GetStringMember(graph, "Start") ?? "start";

        if (!TryGetNodeByName(graph, startNodeName!, out _))
            startNodeName = "start";

        var edgePath = FindShortestEdgePath(graph, startNodeName!, targetNodeName);
        if (edgePath == null || edgePath.Count == 0)
            return null;

        var steps = new List<ConstructionActionStep>();
        if (includeGhostStart)
        {
            var firstNodeAfterGhost = GetNodeName(edgePath[0].To);
            float initialDoAfter = SumEdgeDoAfter(edgePath[0].Edge);
            steps.Add(new ConstructionActionStep(
                ConstructionActionKind.StartGhostConstruction,
                $"Start construction ({constructionProto.ID})",
                PrototypeOrQualityOrTag: constructionProto.ID,
                Amount: 1,
                DoAfterDelay: initialDoAfter,
                ExpectedNextNode: firstNodeAfterGhost));
        }

        foreach (var (fromNode, edge, toNode) in edgePath)
        {
            AppendMachineFrameStepsIfNeeded(null, graph, fromNode, toNode, steps);
            AppendAnchorTransitionStepIfNeeded(graph, fromNode, toNode, isDeconstruct: false, steps);
            ConvertEdgeSteps(edge, GetNodeName(toNode), steps);
        }

        return new SolvedConstructionPlan(
            graph.ID,
            startNodeName!,
            targetNodeName,
            IsDeconstruct: false,
            Steps: steps);
    }

    public SolvedConstructionPlan? SolveDeconstructPlan(EntityUid target)
    {
        if (!_entMan.EntityExists(target))
            return null;

        EnsureCacheInitialized();

        if (!TryResolveGraphAndNode(target, out var graph, out var currentNode, out _) ||
            graph == null || currentNode == null)
        {
            return null;
        }

        var currentNodeName = GetNodeName(currentNode);
        if (string.IsNullOrEmpty(currentNodeName))
            return null;

        var candidateTargets = GetDeconstructCandidateNodes(target, graph, currentNodeName);
        foreach (var targetNodeName in candidateTargets)
        {
            if (string.Equals(targetNodeName, currentNodeName, StringComparison.OrdinalIgnoreCase))
                continue;

            var edgePath = FindShortestEdgePath(graph, currentNodeName, targetNodeName);
            if (edgePath == null || edgePath.Count == 0)
                continue;

            var steps = new List<ConstructionActionStep>();
            var panelComp = FindComponentByName(target, "WiresPanelComponent");
            if (panelComp != null && GetMemberValue(panelComp, "Open") is false)
            {
                var openTool = GetStringMember(panelComp, "OpeningTool") ?? "Screwing";
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.UseTool,
                    $"Open wire panel ({openTool})",
                    PrototypeOrQualityOrTag: openTool,
                    Amount: 1,
                    DoAfterDelay: 1f,
                    ExpectedNextNode: currentNodeName));
            }

            foreach (var (fromNode, edge, toNode) in edgePath)
            {
                AppendAnchorTransitionStepIfNeeded(graph, fromNode, toNode, isDeconstruct: true, steps);
                ConvertEdgeSteps(edge, GetNodeName(toNode), steps);
            }

            if (steps.Count == 0)
                continue;

            return new SolvedConstructionPlan(
                graph.ID,
                currentNodeName,
                targetNodeName,
                IsDeconstruct: true,
                Steps: steps);
        }

        return null;
    }

    public string? ResolveConstructionPrototypeForEntity(string entityProtoId)
    {
        if (string.IsNullOrEmpty(entityProtoId))
            return null;

        EnsureCacheInitialized();

        if (_entityToConstructionProto.TryGetValue(entityProtoId, out var directProtoId))
            return directProtoId;

        if (_protoMan.TryIndex<ConstructionPrototype>(entityProtoId, out var sameIdProto))
            return sameIdProto.ID;

        // Walk EntityPrototype and its parents to see if any maps to a (Graph, Node) -> ConstructionPrototype
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(entityProtoId);
        visited.Add(entityProtoId);

        while (queue.Count > 0)
        {
            var curId = queue.Dequeue();
            if (_entityToConstructionProto.TryGetValue(curId, out var mappedId))
                return mappedId;

            if (_entityToGraphNodes.TryGetValue(curId, out var graphNodes))
            {
                foreach (var (g, n) in graphNodes)
                {
                    if (!_graphToConstructionProtos.TryGetValue(g.ID, out var cProtos) || cProtos.Count == 0)
                        continue;

                    var nodeName = GetNodeName(n);
                    foreach (var cp in cProtos)
                    {
                        if (string.Equals(GetStringMember(cp, "TargetNode"), nodeName, StringComparison.OrdinalIgnoreCase))
                            return cp.ID;
                    }

                    return cProtos[0].ID;
                }
            }

            if (_protoMan.TryIndex<EntityPrototype>(curId, out var eProto) && eProto.Parents != null)
            {
                foreach (var parentId in eProto.Parents)
                {
                    if (visited.Add(parentId))
                        queue.Enqueue(parentId);
                }
            }
        }

        return null;
    }

    public bool TryResolveGraphAndNode(
        EntityUid target,
        out ConstructionGraphPrototype? graph,
        out ConstructionGraphNode? node,
        out string? explicitTargetNode)
    {
        graph = null;
        node = null;
        explicitTargetNode = null;

        if (!_entMan.EntityExists(target))
            return false;

        EnsureCacheInitialized();

        // 1. Check client-visible ConstructionComponent first
        var constructionComp = FindComponentByName(target, "ConstructionComponent");
        if (constructionComp != null)
        {
            var graphId = GetStringMember(constructionComp, "Graph");
            var nodeName = GetStringMember(constructionComp, "Node");
            explicitTargetNode = GetStringMember(constructionComp, "TargetNode");

            if (!string.IsNullOrEmpty(graphId) &&
                _protoMan.TryIndex(graphId, out graph) &&
                !string.IsNullOrEmpty(nodeName) &&
                TryGetNodeByName(graph, nodeName, out node))
            {
                return true;
            }
        }

        // 2. Fallback to EntityPrototype ID -> (Graph, Node) lookup
        var meta = _entMan.GetComponent<MetaDataComponent>(target);
        var proto = meta.EntityPrototype;
        if (proto == null)
            return false;

        // Walk the prototype and its parents
        var visitedProtos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var protoQueue = new Queue<EntityPrototype>();
        protoQueue.Enqueue(proto);
        visitedProtos.Add(proto.ID);

        while (protoQueue.Count > 0)
        {
            var curProto = protoQueue.Dequeue();
            if (_entityToGraphNodes.TryGetValue(curProto.ID, out var candidates) && candidates.Count > 0)
            {
                var chosen = SelectBestNodeCandidate(target, candidates);
                graph = chosen.Graph;
                node = chosen.Node;
                return true;
            }

            if (curProto.Parents != null)
            {
                foreach (var parentId in curProto.Parents)
                {
                    if (visitedProtos.Add(parentId) && _protoMan.TryIndex<EntityPrototype>(parentId, out var parentProto))
                        protoQueue.Enqueue(parentProto);
                }
            }
        }

        return false;
    }

    private (ConstructionGraphPrototype Graph, ConstructionGraphNode Node) SelectBestNodeCandidate(
        EntityUid target,
        List<(ConstructionGraphPrototype Graph, ConstructionGraphNode Node)> candidates)
    {
        if (candidates.Count == 1)
            return candidates[0];

        // Disambiguate multi-node frames like ComputerFrame or AirlockAssembly using container state
        bool hasBoard = _container.TryGetContainer(target, "board", out var boardCont) && boardCont.ContainedEntities.Count > 0;
        if (!hasBoard && _container.TryGetContainer(target, "machine_board", out var mBoardCont) && mBoardCont.ContainedEntities.Count > 0)
            hasBoard = true;

        if (!hasBoard)
        {
            foreach (var candidate in candidates)
            {
                var name = GetNodeName(candidate.Node);
                if (name.Equals("frameUnsecured", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("assembly", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("machineFrame", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("girder", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return candidates[0];
    }

    private string? FindBestBuildTargetNode(ConstructionGraphPrototype graph, ConstructionGraphNode currentNode)
    {
        var currentName = GetNodeName(currentNode);

        // 1. Check ConstructionPrototype recipes associated with this graph
        if (_graphToConstructionProtos.TryGetValue(graph.ID, out var protos))
        {
            string? bestTarget = null;
            int bestDist = int.MaxValue;

            foreach (var cProto in protos)
            {
                var tNode = GetStringMember(cProto, "TargetNode");
                if (string.IsNullOrEmpty(tNode) ||
                    string.Equals(tNode, currentName, StringComparison.OrdinalIgnoreCase) ||
                    NonTerminalBuildNodeNames.Contains(tNode))
                {
                    continue;
                }

                var path = FindShortestEdgePath(graph, currentName, tNode);
                if (path != null && path.Count > 0 && path.Count < bestDist)
                {
                    bestDist = path.Count;
                    bestTarget = tNode;
                }
            }

            if (!string.IsNullOrEmpty(bestTarget))
                return bestTarget;

            // Allow any reachable ConstructionPrototype target node if all were in NonTerminalBuildNodeNames
            foreach (var cProto in protos)
            {
                var tNode = GetStringMember(cProto, "TargetNode");
                if (string.IsNullOrEmpty(tNode) ||
                    string.Equals(tNode, currentName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = FindShortestEdgePath(graph, currentName, tNode);
                if (path != null && path.Count > 0)
                    return tNode;
            }
        }

        // 2. Check well-known terminal node names on the graph (e.g. "machine", "computer", "wall", "airlock", "window")
        string[] commonBuildTargets = { "machine", "computer", "wall", "reinforcedWall", "airlock", "window" };
        foreach (var candidate in commonBuildTargets)
        {
            if (!string.Equals(candidate, currentName, StringComparison.OrdinalIgnoreCase) &&
                TryGetNodeByName(graph, candidate, out _))
            {
                var path = FindShortestEdgePath(graph, currentName, candidate);
                if (path != null && path.Count > 0)
                    return candidate;
            }
        }

        // 3. Fallback: nearest reachable node that produces a constructed entity and isn't a deconstruction/start node
        foreach (var node in EnumerateGraphNodes(graph))
        {
            var nodeName = GetNodeName(node);
            if (string.IsNullOrEmpty(nodeName) ||
                string.Equals(nodeName, currentName, StringComparison.OrdinalIgnoreCase) ||
                PrimaryDeconstructTargets.Contains(nodeName, StringComparer.OrdinalIgnoreCase) ||
                NonTerminalBuildNodeNames.Contains(nodeName))
            {
                continue;
            }

            var path = FindShortestEdgePath(graph, currentName, nodeName);
            if (path != null && path.Count > 0)
                return nodeName;
        }

        return null;
    }

    private List<string> GetDeconstructCandidateNodes(
        EntityUid target,
        ConstructionGraphPrototype graph,
        string currentNodeName)
    {
        var result = new List<string>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? name)
        {
            if (string.IsNullOrEmpty(name) ||
                string.Equals(name, currentNodeName, StringComparison.OrdinalIgnoreCase))
                return;

            if (TryGetNodeByName(graph, name, out _) && added.Add(name))
                result.Add(name);
        }

        var constructionComp = FindComponentByName(target, "ConstructionComponent");
        if (constructionComp != null)
            TryAdd(GetStringMember(constructionComp, "DeconstructionNode"));

        TryAdd(GetStringMember(graph, "Start"));

        foreach (var primary in PrimaryDeconstructTargets)
            TryAdd(primary);

        // Any node whose Entity is null/empty (such as "start" or a deleted-entity terminal node)
        foreach (var n in EnumerateGraphNodes(graph))
        {
            var name = GetNodeName(n);
            if (string.IsNullOrEmpty(GetNodeEntityId(n, null)) &&
                GetNodeEdges(n).Count == 0)
            {
                TryAdd(name);
            }
        }

        foreach (var fallback in FallbackDeconstructTargets)
            TryAdd(fallback);

        return result;
    }

    private List<(ConstructionGraphNode From, ConstructionGraphEdge Edge, ConstructionGraphNode To)>? FindShortestEdgePath(
        ConstructionGraphPrototype graph,
        string startNodeName,
        string targetNodeName)
    {
        if (!TryGetNodeByName(graph, startNodeName, out var startNode) ||
            !TryGetNodeByName(graph, targetNodeName, out var targetNode))
        {
            return null;
        }

        var queue = new Queue<ConstructionGraphNode>();
        var cameFrom = new Dictionary<string, (ConstructionGraphNode From, ConstructionGraphEdge Edge)>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var startName = GetNodeName(startNode);
        queue.Enqueue(startNode);
        visited.Add(startName);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentName = GetNodeName(current);

            if (string.Equals(currentName, targetNodeName, StringComparison.OrdinalIgnoreCase))
            {
                var path = new List<(ConstructionGraphNode From, ConstructionGraphEdge Edge, ConstructionGraphNode To)>();
                var traceNode = targetNode;
                var traceName = GetNodeName(traceNode);

                while (!string.Equals(traceName, startName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!cameFrom.TryGetValue(traceName, out var prev))
                        return null;

                    path.Add((prev.From, prev.Edge, traceNode));
                    traceNode = prev.From;
                    traceName = GetNodeName(traceNode);
                }

                path.Reverse();
                return path;
            }

            foreach (var edge in GetNodeEdges(current))
            {
                var edgeTargetName = GetStringMember(edge, "Target");
                if (string.IsNullOrEmpty(edgeTargetName) || !visited.Add(edgeTargetName))
                    continue;

                if (!TryGetNodeByName(graph, edgeTargetName, out var nextNode))
                    continue;

                cameFrom[edgeTargetName] = (current, edge);
                queue.Enqueue(nextNode);
            }
        }

        return null;
    }

    private float SumEdgeDoAfter(ConstructionGraphEdge edge)
    {
        float total = 0f;
        var tmp = new List<ConstructionActionStep>();
        ConvertEdgeSteps(edge, string.Empty, tmp);
        foreach (var s in tmp)
            total += s.DoAfterDelay;
        return total;
    }

    private void AppendAnchorTransitionStepIfNeeded(
        ConstructionGraphPrototype graph,
        ConstructionGraphNode fromNode,
        ConstructionGraphNode toNode,
        bool isDeconstruct,
        List<ConstructionActionStep> steps)
    {
        var fromName = GetNodeName(fromNode);
        var toName = GetNodeName(toNode);

        if (isDeconstruct)
        {
            // Unanchoring before final deconstruction to start node for structures that require anchored: false
            if (string.Equals(toName, "start", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(fromName, "girder", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(fromName, "assembly", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(fromName, "frameUnsecured", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(fromName, "missingWires", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(fromName, "frame", StringComparison.OrdinalIgnoreCase)))
            {
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.UseTool,
                    "Unanchor structure (Anchoring)",
                    PrototypeOrQualityOrTag: "Anchoring",
                    Amount: 1,
                    DoAfterDelay: 1f,
                    ExpectedNextNode: null));
            }
        }
        else
        {
            // Anchoring frames/assemblies that spawn unanchored (skipped automatically at runtime if already anchored)
            if (string.Equals(fromName, "assembly", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fromName, "missingWires", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fromName, "frameUnsecured", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fromName, "girder", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fromName, "frame", StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(new ConstructionActionStep(
                    ConstructionActionKind.UseTool,
                    "Anchor frame (Anchoring)",
                    PrototypeOrQualityOrTag: "Anchoring",
                    Amount: 1,
                    DoAfterDelay: 1f,
                    ExpectedNextNode: null));
            }
        }
    }

    private void AppendMachineFrameStepsIfNeeded(
        EntityUid? target,
        ConstructionGraphPrototype graph,
        ConstructionGraphNode fromNode,
        ConstructionGraphNode toNode,
        List<ConstructionActionStep> steps)
    {
        if (!string.Equals(graph.ID, "Machine", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetNodeName(fromNode), "machineFrame", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetNodeName(toNode), "machine", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        EntityUid? boardUid = null;
        if (target.HasValue &&
            _entMan.EntityExists(target.Value) &&
            _container.TryGetContainer(target.Value, "machine_board", out var boardContainer) &&
            boardContainer.ContainedEntities.Count > 0)
        {
            boardUid = boardContainer.ContainedEntities[0];
        }

        if (boardUid == null)
        {
            steps.Add(new ConstructionActionStep(
                ConstructionActionKind.InsertComponent,
                "Insert MachineBoard",
                PrototypeOrQualityOrTag: "MachineBoard",
                Amount: 1,
                DoAfterDelay: 0f,
                ExpectedNextNode: "machineFrame"));
            return;
        }

        var boardComp = FindComponentByName(boardUid.Value, "MachineBoardComponent");
        if (boardComp == null)
            return;

        if (GetMemberValue(boardComp, "StackRequirements") is IDictionary stackReqs)
        {
            foreach (DictionaryEntry entry in stackReqs)
            {
                var stackId = entry.Key?.ToString();
                var amount = entry.Value is int i ? i : 1;
                if (amount > 0 && !string.IsNullOrEmpty(stackId))
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.AddMaterial,
                        $"Add {amount}x {stackId}",
                        PrototypeOrQualityOrTag: stackId,
                        Amount: amount,
                        DoAfterDelay: 0f,
                        ExpectedNextNode: "machineFrame"));
                }
            }
        }

        if (GetMemberValue(boardComp, "ComponentRequirements") is IDictionary compReqs)
        {
            foreach (DictionaryEntry entry in compReqs)
            {
                var compName = entry.Key?.ToString();
                var amt = GetIntMember(entry.Value, "Amount", 1);
                if (amt > 0 && !string.IsNullOrEmpty(compName))
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.InsertComponent,
                        $"Insert {amt}x {compName}",
                        PrototypeOrQualityOrTag: compName,
                        Amount: amt,
                        DoAfterDelay: 0f,
                        ExpectedNextNode: "machineFrame"));
                }
            }
        }

        if (GetMemberValue(boardComp, "TagRequirements") is IDictionary tagReqs)
        {
            foreach (DictionaryEntry entry in tagReqs)
            {
                var tagId = entry.Key?.ToString();
                var amt = GetIntMember(entry.Value, "Amount", 1);
                if (amt > 0 && !string.IsNullOrEmpty(tagId))
                {
                    steps.Add(new ConstructionActionStep(
                        ConstructionActionKind.InsertTag,
                        $"Insert {amt}x [{tagId}]",
                        PrototypeOrQualityOrTag: tagId,
                        Amount: amt,
                        DoAfterDelay: 0f,
                        ExpectedNextNode: "machineFrame"));
                }
            }
        }
    }

    private void ConvertEdgeSteps(
        ConstructionGraphEdge edge,
        string expectedNextNode,
        List<ConstructionActionStep> output)
    {
        var rawSteps = GetMemberValue(edge, "Steps") as IEnumerable ?? GetMemberValue(edge, "_steps") as IEnumerable;
        if (rawSteps == null)
            return;

        foreach (var stepObj in rawSteps)
        {
            if (stepObj != null)
                ConvertSingleOrNestedStep(stepObj, expectedNextNode, output);
        }
    }

    private void ConvertSingleOrNestedStep(
        object stepObj,
        string expectedNextNode,
        List<ConstructionActionStep> output)
    {
        var type = stepObj.GetType();
        var typeName = type.Name;

        // Handle NestedConstructionGraphStep (which stores nested step collections)
        if (typeName.Contains("NestedConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var nestedEnumerable = GetMemberValue(stepObj, "Steps") as IEnumerable
                                   ?? GetMemberValue(stepObj, "_steps") as IEnumerable;
            if (nestedEnumerable != null)
            {
                foreach (var sub in nestedEnumerable)
                {
                    if (sub == null)
                        continue;

                    if (sub is IEnumerable subList && sub is not string)
                    {
                        foreach (var innerStep in subList)
                        {
                            if (innerStep != null)
                                ConvertSingleOrNestedStep(innerStep, expectedNextNode, output);
                        }
                    }
                    else
                    {
                        ConvertSingleOrNestedStep(sub, expectedNextNode, output);
                    }
                }
            }
            return;
        }

        float doAfter = GetFloatMember(stepObj, "DoAfter", 0f);
        int amount = Math.Max(1, GetIntMember(stepObj, "Amount", 1));

        // 1. MaterialConstructionGraphStep
        if (typeName.Contains("MaterialConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var materialId = GetStringMember(stepObj, "MaterialPrototypeId")
                             ?? GetStringMember(stepObj, "Material")
                             ?? "Steel";
            output.Add(new ConstructionActionStep(
                ConstructionActionKind.AddMaterial,
                $"Add {amount}x {materialId}",
                PrototypeOrQualityOrTag: materialId,
                Amount: amount,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
            return;
        }

        // 2. ToolConstructionGraphStep
        if (typeName.Contains("ToolConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var toolQuality = GetStringMember(stepObj, "Tool") ?? "Wrenching";
            output.Add(new ConstructionActionStep(
                ConstructionActionKind.UseTool,
                $"Use {toolQuality} tool",
                PrototypeOrQualityOrTag: toolQuality,
                Amount: 1,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
            return;
        }

        // 3. PrototypeConstructionGraphStep
        if (typeName.Contains("PrototypeConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var protoId = GetStringMember(stepObj, "Prototype")
                          ?? GetStringMember(stepObj, "PrototypeId")
                          ?? string.Empty;
            output.Add(new ConstructionActionStep(
                ConstructionActionKind.InsertPrototype,
                $"Insert {amount}x {protoId}",
                PrototypeOrQualityOrTag: protoId,
                Amount: amount,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
            return;
        }

        // 4. ComponentConstructionGraphStep
        if (typeName.Contains("ComponentConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var compName = GetStringMember(stepObj, "Component") ?? string.Empty;
            output.Add(new ConstructionActionStep(
                ConstructionActionKind.InsertComponent,
                $"Insert {amount}x [{compName}]",
                PrototypeOrQualityOrTag: compName,
                Amount: amount,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
            return;
        }

        // 5. TagConstructionGraphStep / MultipleTagsConstructionGraphStep
        if (typeName.Contains("TagConstructionGraphStep", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("MultipleTagsConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var tag = GetStringMember(stepObj, "Tag") ?? GetStringMember(stepObj, "_tag");
            if (string.IsNullOrEmpty(tag))
            {
                tag = FirstElementAsString(GetMemberValue(stepObj, "AllTags") ?? GetMemberValue(stepObj, "_allTags"))
                      ?? FirstElementAsString(GetMemberValue(stepObj, "AnyTags") ?? GetMemberValue(stepObj, "_anyTags"))
                      ?? string.Empty;
            }

            output.Add(new ConstructionActionStep(
                ConstructionActionKind.InsertTag,
                $"Insert {amount}x tag:{tag}",
                PrototypeOrQualityOrTag: tag,
                Amount: amount,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
        }
    }

    private IEnumerable<ConstructionGraphNode> EnumerateGraphNodes(ConstructionGraphPrototype graph)
    {
        var nodesDict = GetMemberValue(graph, "Nodes") as IDictionary ?? GetMemberValue(graph, "_nodes") as IDictionary;
        if (nodesDict != null)
        {
            foreach (var val in nodesDict.Values)
            {
                if (val is ConstructionGraphNode node)
                    yield return node;
            }
            yield break;
        }

        if (GetMemberValue(graph, "_graph") is IEnumerable graphList)
        {
            foreach (var val in graphList)
            {
                if (val is ConstructionGraphNode node)
                    yield return node;
            }
        }
    }

    private bool TryGetNodeByName(ConstructionGraphPrototype graph, string nodeName, out ConstructionGraphNode node)
    {
        foreach (var n in EnumerateGraphNodes(graph))
        {
            if (string.Equals(GetNodeName(n), nodeName, StringComparison.OrdinalIgnoreCase))
            {
                node = n;
                return true;
            }
        }

        node = default!;
        return false;
    }

    private static string GetNodeName(ConstructionGraphNode node)
        => GetStringMember(node, "Name") ?? string.Empty;

    private static IReadOnlyList<ConstructionGraphEdge> GetNodeEdges(ConstructionGraphNode node)
    {
        var edgesObj = GetMemberValue(node, "Edges") ?? GetMemberValue(node, "_edges");
        if (edgesObj is IReadOnlyList<ConstructionGraphEdge> roList)
            return roList;

        if (edgesObj is IEnumerable enumerable)
        {
            var list = new List<ConstructionGraphEdge>();
            foreach (var item in enumerable)
            {
                if (item is ConstructionGraphEdge edge)
                    list.Add(edge);
            }
            return list;
        }

        return Array.Empty<ConstructionGraphEdge>();
    }

    private string? GetNodeEntityId(ConstructionGraphNode node, EntityUid? uid)
    {
        var entityObj = GetMemberValue(node, "Entity") ?? GetMemberValue(node, "_entity");
        if (entityObj == null)
            return null;

        if (entityObj is string str)
            return string.IsNullOrEmpty(str) ? null : str;

        var directId = GetStringMember(entityObj, "Id") ?? GetStringMember(entityObj, "Prototype");
        if (!string.IsNullOrEmpty(directId))
            return directId;

        var getIdMethod = entityObj.GetType().GetMethod("GetId", BindingFlags.Public | BindingFlags.Instance);
        if (getIdMethod != null)
        {
            try
            {
                var parameters = getIdMethod.GetParameters();
                var args = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var pType = parameters[i].ParameterType;
                    if (pType == typeof(EntityUid?) || pType == typeof(EntityUid))
                        args[i] = uid;
                    else if (pType == typeof(IEntityManager))
                        args[i] = _entMan;
                    else if (pType == typeof(IComponentFactory))
                        args[i] = _compFactory;
                    else if (pType.Name == "GraphNodeEntityArgs")
                        args[i] = Activator.CreateInstance(pType, _entMan);
                    else
                        args[i] = null;
                }

                var res = getIdMethod.Invoke(entityObj, args)?.ToString();
                if (!string.IsNullOrEmpty(res))
                    return res;
            }
            catch
            {
                // Ignore if node entity requires server-only state
            }
        }

        return null;
    }

    private IComponent? FindComponentByName(EntityUid uid, string componentTypeName)
    {
        foreach (var comp in _entMan.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, componentTypeName, StringComparison.OrdinalIgnoreCase))
                return comp;
        }
        return null;
    }

    private static object? GetMemberValue(object? obj, string name)
    {
        if (obj == null)
            return null;

        var type = obj.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        var prop = type.GetProperty(name, flags);
        if (prop != null)
            return prop.GetValue(obj);

        var field = type.GetField(name, flags);
        if (field != null)
            return field.GetValue(obj);

        return null;
    }

    private static string? GetStringMember(object? obj, string name)
    {
        var val = GetMemberValue(obj, name);
        if (val == null)
            return null;
        if (val is string s)
            return s;
        if (GetMemberValue(val, "Id") is string idStr)
            return idStr;
        return val.ToString();
    }

    private static int GetIntMember(object? obj, string name, int defaultValue = 0)
    {
        var val = GetMemberValue(obj, name);
        if (val == null)
            return defaultValue;
        if (val is int i)
            return i;
        try
        {
            return Convert.ToInt32(val);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static float GetFloatMember(object? obj, string name, float defaultValue = 0f)
    {
        var val = GetMemberValue(obj, name);
        if (val == null)
            return defaultValue;
        if (val is float f)
            return f;
        if (val is double d)
            return (float)d;
        if (val is int i)
            return i;
        try
        {
            return Convert.ToSingle(val);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static string? FirstElementAsString(object? collectionObj)
    {
        if (collectionObj is IEnumerable enumerable && collectionObj is not string)
        {
            foreach (var item in enumerable)
            {
                var s = item?.ToString();
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }
        return null;
    }
}
