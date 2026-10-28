using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Content.Client.Construction;
using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;
using Content.Shared.DoAfter;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.Tag;
using Content.Shared.Tools.Systems;
using Content.Shared.Wires;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using RobustoClient.Systems.Abstract;
using RobustoClient.Systems.Construction.Blueprints;
using RobustoClient.Systems.Hacking;

namespace RobustoClient.Systems.Construction;

public sealed class RobustaAutoConstructSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IInputManager _input = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly IPrototypeManager _protoMan = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedToolSystem _toolSystem = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly ItemUtilitySystem _itemUtil = default!;
    [Dependency] private readonly RobustaConstructionSolver _solver = default!;
    [Dependency] private readonly ConstructionSystem _clientConstruction = default!;
    [Dependency] private readonly RobustaWiresSolver _wiresSolver = default!;

    private ISawmill _log = default!;

    private const float MaxInteractRange = 2.5f;
    private const float BlueprintBuildInteractRange = 1.55f;
    private const float SettleDelay = 0.25f;
    private const float TargetRescanTimeout = 2.5f;
    private const float DoubleTapOrHoldWindow = 0.25f;

    private bool _pendingSingleTap;
    private float _singleTapTimer;
    private bool _wasConstructKeyDown;
    private bool _wasDeconstructKeyDown;

    private enum StepExecutionPhase
    {
        PrepareAndEquip,
        EnsureWelderLit,
        PerformInteraction,
        WaitForCompletion
    }

    private SolvedConstructionPlan? _activePlan;
    private int _stepIndex;
    private StepExecutionPhase _phase;
    private EntityUid _currentTarget;
    private EntityCoordinates _targetCoordinates;
    private MapCoordinates _targetMapCoords;
    private float _waitTimer;
    private float _rescanTimer;
    private bool _wasInDoAfter;
    private EntityUid? _litWelderUid;
    private EntityUid _stepStartTarget;
    private string? _stepStartNode;
    private EntityUid? _stepStartHeldItem;
    private int _stepStartHeldStackCount;
    private int _stepRetryCount;
    private int _equipRetryCount;
    private int _welderIgniteRetries;
    private bool _sawDoAfterForStep;
    private bool _doAfterCancelledForStep;
    private float _stepWaitElapsed;
    private float _postDoAfterTimer;
    private bool _delegatedWireSolveStarted;
    private readonly HashSet<EntityUid> _preExistingTileEntities = new();

    private readonly List<(EntityUid GridUid, Vector2i Tile, Direction Dir, BlueprintItemEntry Item)> _blueprintQueue = new();
    public IReadOnlyList<(EntityUid GridUid, Vector2i Tile, Direction Dir, BlueprintItemEntry Item)> PendingBlueprintTasks => _blueprintQueue;
    public BlueprintZPhase? CurrentActiveBlueprintPhase => _blueprintQueue.Count > 0 ? _blueprintQueue.Min(t => GetEffectiveBlueprintPhase(t.Item)) : null;
    public BlueprintZPhase? ActivePhaseFilter { get; set; }
    public bool AutoBuildPlacedBlueprints { get; set; } = true;

    private static readonly string[] UnfinishedFrameKeywords =
    {
        "girder",
        "machineframe",
        "computerframe",
        "unfinished",
        "assembly",
        "apcframe",
        "wallframe",
        "windowframe",
        "frame"
    };

    public bool IsBusy => _activePlan != null || _blueprintQueue.Count > 0;

    public override void Initialize()
    {
        base.Initialize();
        _log = Logger.GetSawmill("robusta.autoconstruct");
    }

    public void HandleConstructKeyTap()
    {
        // Ignore OS key-repeat events while the construct key is held down
        if (_wasConstructKeyDown)
            return;
        _wasConstructKeyDown = true;

        if (_activePlan != null || _blueprintQueue.Count > 0)
        {
            _pendingSingleTap = false;
            _singleTapTimer = 0f;
            _log.Info("Aborting active AutoConstruct job.");
            StopJob();
            return;
        }

        // Shift + V immediately triggers Deconstruct
        if (_input.IsKeyDown(Keyboard.Key.Shift))
        {
            _pendingSingleTap = false;
            _singleTapTimer = 0f;
            TriggerAtCursor(forceDeconstruct: true);
            return;
        }

        // Second tap within window -> Double-tap Deconstruct!
        if (_pendingSingleTap)
        {
            _pendingSingleTap = false;
            _singleTapTimer = 0f;
            TriggerAtCursor(forceDeconstruct: true);
            return;
        }

        // Start short window to distinguish 1 short tap (Build/Auto) vs double-tap / long-hold (Deconstruct)
        _pendingSingleTap = true;
        _singleTapTimer = DoubleTapOrHoldWindow;
    }

    public void HandleDeconstructKeyTap()
    {
        // Ignore OS key-repeat events while the deconstruct key is held down
        if (_wasDeconstructKeyDown)
            return;
        _wasDeconstructKeyDown = true;

        TriggerAtCursor(forceDeconstruct: true);
    }

    public void TriggerAtCursor(bool? forceDeconstruct = null)
    {
        _pendingSingleTap = false;
        _singleTapTimer = 0f;

        if (_activePlan != null || _blueprintQueue.Count > 0)
        {
            _log.Info("Aborting active AutoConstruct job.");
            StopJob();
            return;
        }

        if (_player.LocalEntity is not { } player || !Exists(player))
            return;

        _solver.NoteHeldEntity(_hands.GetActiveItem(player));

        var mouseScreenPos = _input.MouseScreenPosition;
        var cursorMapCoords = _eye.PixelToMap(mouseScreenPos);
        if (cursorMapCoords.MapId == MapId.Nullspace)
            return;

        var playerMapCoords = _transform.GetMapCoordinates(player);
        if (playerMapCoords.MapId != cursorMapCoords.MapId)
            return;

        var target = FindTargetAtCursor(player, cursorMapCoords);
        if (target == null || !Exists(target.Value))
        {
            _log.Info("No constructible or deconstructible entity found at cursor.");
            return;
        }

        SolvedConstructionPlan? plan = null;
        if (forceDeconstruct == true)
        {
            plan = _solver.SolveDeconstructPlan(target.Value);
        }
        else if (forceDeconstruct == false)
        {
            plan = _solver.SolveBuildPlan(target.Value) ?? _solver.SolveDeconstructPlan(target.Value);
        }
        else
        {
            if (IsGhostOrUnfinishedFrame(target.Value))
            {
                plan = _solver.SolveBuildPlan(target.Value) ?? _solver.SolveDeconstructPlan(target.Value);
            }
            else
            {
                plan = _solver.SolveDeconstructPlan(target.Value) ?? _solver.SolveBuildPlan(target.Value);
            }
        }

        if (plan == null || plan.Steps.Count == 0)
        {
            _log.Warning($"Could not solve construction plan for {ToPrettyString(target.Value)}.");
            return;
        }

        StartJob(target.Value, plan);
    }

    public void StartJob(EntityUid target, SolvedConstructionPlan plan)
    {
        if (!Exists(target) || plan.Steps.Count == 0)
            return;

        _activePlan = plan;
        _stepIndex = 0;
        _phase = StepExecutionPhase.PrepareAndEquip;
        _currentTarget = target;
        _targetCoordinates = Transform(target).Coordinates;
        _targetMapCoords = _transform.GetMapCoordinates(target);
        _preExistingTileEntities.Clear();
        if (_targetMapCoords.MapId != MapId.Nullspace)
        {
            foreach (var uid in _lookup.GetEntitiesInRange(_targetMapCoords, 0.45f, LookupFlags.Uncontained))
            {
                if (uid != target && Exists(uid) && !Deleted(uid) && !HasGhostComponent(uid))
                    _preExistingTileEntities.Add(uid);
            }
        }
        _waitTimer = 0f;
        _rescanTimer = 0f;
        _wasInDoAfter = false;
        _stepRetryCount = 0;
        _equipRetryCount = 0;
        _welderIgniteRetries = 0;
        _sawDoAfterForStep = false;
        _doAfterCancelledForStep = false;
        _stepWaitElapsed = 0f;
        _postDoAfterTimer = 0f;
        _delegatedWireSolveStarted = false;

        _log.Info($"Started {(plan.IsDeconstruct ? "Deconstruct" : "Build")} plan on {ToPrettyString(target)} " +
                  $"({plan.GraphId}: {plan.StartNode} -> {plan.TargetNode}, {plan.Steps.Count} steps).");
    }

    public void StopJob()
    {
        _blueprintQueue.Clear();
        FinishCurrentPlan(cancelled: true);
    }

    private void FinishCurrentPlan(bool cancelled = false)
    {
        _itemUtil.CancelAll();
        if (cancelled)
        {
            ExtinguishActiveWelderIfNeeded();
        }
        else if (_blueprintQueue.Count == 0)
        {
            _litWelderUid = null;
            _itemUtil.StowActiveTrackedItem();
        }
        else
        {
            ExtinguishActiveWelderIfNeeded();
        }

        if (Exists(_currentTarget) && HasGhostComponent(_currentTarget))
        {
            ClearGhostEntity(_currentTarget);
        }

        _preExistingTileEntities.Clear();
        _activePlan = null;
        _stepIndex = 0;
        _phase = StepExecutionPhase.PrepareAndEquip;
        _currentTarget = EntityUid.Invalid;
        _waitTimer = 0f;
        _rescanTimer = 0f;
        _wasInDoAfter = false;
        _stepStartTarget = EntityUid.Invalid;
        _stepStartNode = null;
        _stepStartHeldItem = null;
        _stepStartHeldStackCount = 0;
        _stepRetryCount = 0;
        _equipRetryCount = 0;
        _welderIgniteRetries = 0;
        _sawDoAfterForStep = false;
        _doAfterCancelledForStep = false;
        _stepWaitElapsed = 0f;
        _postDoAfterTimer = 0f;
        if (_delegatedWireSolveStarted)
        {
            _wiresSolver.StopSolving();
            _delegatedWireSolveStarted = false;
        }
    }

    public void EnqueuePhasedBlueprintBuild(
        RobustaBlueprint blueprint,
        EntityCoordinates origin,
        Direction rotation,
        BlueprintZPhase? onlyPhase = null)
        => StartPhasedBlueprintBuild(blueprint, origin, rotation, onlyPhase);

    public void StartPhasedBlueprintBuild(
        RobustaBlueprint blueprint,
        EntityCoordinates origin,
        Direction rotation,
        BlueprintZPhase? onlyPhase = null)
    {
        var gridUid = origin.EntityId;
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
        {
            var foundGrid = _transform.GetGrid(origin);
            if (foundGrid == null || !TryComp(foundGrid.Value, out grid))
            {
                _log.Warning("Cannot start phased blueprint build: target coordinates are not on a valid grid.");
                return;
            }
            gridUid = foundGrid.Value;
        }

        var baseTile = _mapSystem.TileIndicesFor(gridUid, grid, origin);
        int rotSteps = BlueprintPlacementHijack.DirectionToSteps(rotation);

        var items = blueprint.Items
            .Where(i => onlyPhase == null || i.Phase == onlyPhase.Value || GetEffectiveBlueprintPhase(i) == onlyPhase.Value)
            .OrderBy(i => GetEffectiveBlueprintPhase(i))
            .ThenBy(i => GetEffectiveBlueprintSubPriority(i))
            .ThenBy(i => i.LayerOrder)
            .ThenBy(i => i.OffsetY)
            .ThenBy(i => i.OffsetX)
            .ToList();

        if (items.Count == 0)
            return;

        int added = 0;
        foreach (var item in items)
        {
            var (rx, ry) = BlueprintPlacementHijack.RotateOffset(
                item.OffsetX,
                item.OffsetY,
                blueprint.Width,
                blueprint.Height,
                rotSteps);
            var tile = baseTile + new Vector2i(rx, ry);
            var dir = BlueprintPlacementHijack.RotateDirection(
                BlueprintPlacementHijack.ParseDirection(item.Direction),
                rotSteps);

            _blueprintQueue.Add((gridUid, tile, dir, item));
            added++;
        }

        _blueprintQueue.Sort((a, b) =>
        {
            int c = GetEffectiveBlueprintPhase(a.Item).CompareTo(GetEffectiveBlueprintPhase(b.Item));
            if (c != 0) return c;
            c = GetEffectiveBlueprintSubPriority(a.Item).CompareTo(GetEffectiveBlueprintSubPriority(b.Item));
            if (c != 0) return c;
            return a.Item.LayerOrder.CompareTo(b.Item.LayerOrder);
        });

        _log.Info($"Queued {added} phased blueprint build tasks for '{blueprint.Name}' (Phase filter: {onlyPhase?.ToString() ?? "All"}).");
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (!_input.IsKeyDown(RobustaConfig.AutoConstructKey))
            _wasConstructKeyDown = false;
        if (!_input.IsKeyDown(RobustaConfig.AutoDeconstructKey))
            _wasDeconstructKeyDown = false;

        if (_pendingSingleTap)
        {
            _singleTapTimer -= frameTime;
            if (_singleTapTimer <= 0f)
            {
                _pendingSingleTap = false;
                _singleTapTimer = 0f;
                // If user is still holding the key at the end of the window -> Long-press Deconstruct;
                // otherwise -> Auto-detect (builds ghosts/unfinished frames, deconstructs completed structures)
                bool isHolding = _input.IsKeyDown(RobustaConfig.AutoConstructKey);
                TriggerAtCursor(forceDeconstruct: isHolding ? true : null);
                return;
            }
        }

        if (_activePlan == null && _blueprintQueue.Count == 0)
            return;

        if (_player.LocalEntity is not { } player || !Exists(player))
        {
            StopJob();
            return;
        }

        _solver.NoteHeldEntity(_hands.GetActiveItem(player));

        // Check DoAfter state BEFORE _itemUtil.IsBusy so short DoAfters starting during post-click delay are never missed
        if (IsPlayerInActiveDoAfter(player))
        {
            _wasInDoAfter = true;
            _sawDoAfterForStep = true;
            return;
        }

        if (_wasInDoAfter)
        {
            _wasInDoAfter = false;
            _waitTimer = Math.Max(_waitTimer, 0.35f);
        }

        if (_itemUtil.IsBusy)
            return;

        if (_waitTimer > 0f)
        {
            _waitTimer -= frameTime;
            return;
        }

        if (_activePlan == null)
        {
            ProcessNextQueuedBlueprintTask(player);
            return;
        }

        ExecuteCurrentStep(player, frameTime);
    }

    private void ProcessNextQueuedBlueprintTask(EntityUid player)
    {
        var playerMapCoords = _transform.GetMapCoordinates(player);

        while (_blueprintQueue.Count > 0)
        {
            // Prune invalid grids or already-built items in the current earliest effective phase
            var minPhase = _blueprintQueue.Min(t => GetEffectiveBlueprintPhase(t.Item));
            bool prunedAny = false;

            for (int i = _blueprintQueue.Count - 1; i >= 0; i--)
            {
                var candidate = _blueprintQueue[i];
                if (GetEffectiveBlueprintPhase(candidate.Item) != minPhase)
                    continue;

                if (!Exists(candidate.GridUid) || !TryComp<MapGridComponent>(candidate.GridUid, out var cGrid))
                {
                    _blueprintQueue.RemoveAt(i);
                    prunedAny = true;
                    continue;
                }

                var cCoords = _mapSystem.GridTileToLocal(candidate.GridUid, cGrid, candidate.Tile);
                var cMap = _transform.ToMapCoordinates(cCoords);
                if (cMap.MapId != playerMapCoords.MapId ||
                    IsBlueprintItemAlreadySatisfied(cMap, candidate.Item, candidate.Dir))
                {
                    _blueprintQueue.RemoveAt(i);
                    prunedAny = true;
                }
            }

            if (_blueprintQueue.Count == 0)
                return;

            if (prunedAny)
                minPhase = _blueprintQueue.Min(t => GetEffectiveBlueprintPhase(t.Item));

            // Determine the earliest sub-priority still queued on each tile so a pipe (SubPriority 2)
            // is NEVER built on a tile while a filter/mixer/pump/port/vent (SubPriority 1) is still queued on that tile.
            var minSubPriorityPerTile = new Dictionary<(EntityUid GridUid, Vector2i Tile), int>();
            foreach (var entry in _blueprintQueue)
            {
                if (GetEffectiveBlueprintPhase(entry.Item) != minPhase)
                    continue;

                var key = (entry.GridUid, entry.Tile);
                int sub = GetEffectiveBlueprintSubPriority(entry.Item);
                if (!minSubPriorityPerTile.TryGetValue(key, out int curMin) || sub < curMin)
                    minSubPriorityPerTile[key] = sub;
            }

            // Pick the best in-range task in minPhase:
            // 1. Must be at the earliest pending SubPriority for its tile (so contraptions on a tile ALWAYS build before pipes on that tile)
            // 2. Prefer lower SubPriority (all in-range contraptions before in-range pipes), then nearest distance
            int bestIdx = -1;
            int bestSubPriority = int.MaxValue;
            float bestDist = float.MaxValue;
            int bestLayerOrder = int.MaxValue;

            for (int i = 0; i < _blueprintQueue.Count; i++)
            {
                var candidate = _blueprintQueue[i];
                if (GetEffectiveBlueprintPhase(candidate.Item) != minPhase ||
                    !Exists(candidate.GridUid) ||
                    !TryComp<MapGridComponent>(candidate.GridUid, out var cGrid))
                    continue;

                int subPriority = GetEffectiveBlueprintSubPriority(candidate.Item);
                if (minSubPriorityPerTile.TryGetValue((candidate.GridUid, candidate.Tile), out int tileMinSub) &&
                    subPriority > tileMinSub)
                {
                    // Hold off on pipes on this tile until the contraption(s) on this tile are built first
                    continue;
                }

                var cCoords = _mapSystem.GridTileToLocal(candidate.GridUid, cGrid, candidate.Tile);
                var cMap = _transform.ToMapCoordinates(cCoords);
                if (cMap.MapId != playerMapCoords.MapId)
                    continue;

                float d = (cMap.Position - playerMapCoords.Position).Length();
                if (d > BlueprintBuildInteractRange)
                    continue;

                if (subPriority < bestSubPriority ||
                    (subPriority == bestSubPriority && d < bestDist - 0.05f) ||
                    (subPriority == bestSubPriority && MathF.Abs(d - bestDist) <= 0.05f && candidate.Item.LayerOrder < bestLayerOrder))
                {
                    bestSubPriority = subPriority;
                    bestDist = d;
                    bestLayerOrder = candidate.Item.LayerOrder;
                    bestIdx = i;
                }
            }

            // If no eligible task in this phase is within SS14's construction interaction range,
            // keep remaining tasks in the queue (and visible on the overlay) until the player steps closer.
            if (bestIdx < 0)
                return;

            var task = _blueprintQueue[bestIdx];
            _blueprintQueue.RemoveAt(bestIdx);

            var grid = Comp<MapGridComponent>(task.GridUid);
            var tileCoords = _mapSystem.GridTileToLocal(task.GridUid, grid, task.Tile);
            var tileMapCoords = _transform.ToMapCoordinates(tileCoords);

            // 1. Direct stack placement (Cables, Floor Tiles, Rods/Lattice)
            if (TryExecuteDirectStackBlueprintItem(task.Item, tileCoords))
            {
                _waitTimer = SettleDelay;
                return;
            }

            var recipeId = task.Item.ConstructionPrototypeId;
            var resolvedFromEntity = _solver.ResolveConstructionPrototypeForEntity(task.Item.EntityPrototypeId);
            if (string.IsNullOrEmpty(recipeId) || !_protoMan.HasIndex<ConstructionPrototype>(recipeId))
            {
                recipeId = resolvedFromEntity;
            }
            else if (!string.IsNullOrEmpty(resolvedFromEntity) &&
                     !string.Equals(recipeId, resolvedFromEntity, StringComparison.OrdinalIgnoreCase) &&
                     _solver.GetDesiredPipeLayer(task.Item.EntityPrototypeId, null) > 0 &&
                     RobustaConstructionSolver.GetPipeLayerForIdOrNode(resolvedFromEntity) > 0)
            {
                recipeId = resolvedFromEntity;
            }

            // 2. Existing unfinished frame or matching ghost on the tile (clearing any mismatched ghosts)
            var existingFrameOrGhost = FindMatchingFrameOrGhostOnTile(tileMapCoords, task.Item, task.Dir, recipeId);
            if (existingFrameOrGhost != null && Exists(existingFrameOrGhost.Value))
            {
                var existingPlan = _solver.SolveBuildPlan(existingFrameOrGhost.Value, task.Item.EntityPrototypeId);
                if (existingPlan != null && existingPlan.Steps.Count > 0)
                {
                    StartJob(existingFrameOrGhost.Value, existingPlan);
                    return;
                }
            }

            // 3. Ensure no leftover ghost is occupying the tile, then spawn this item's construction ghost on-demand
            ClearGhostsOnTile(tileMapCoords);

            if (!string.IsNullOrEmpty(recipeId) &&
                _protoMan.TryIndex<ConstructionPrototype>(recipeId, out var cProto) &&
                cProto.Type == ConstructionType.Structure)
            {
                if (_clientConstruction.TrySpawnGhost(cProto, tileCoords, task.Dir, out var ghostUid) &&
                    ghostUid != null && Exists(ghostUid.Value))
                {
                    var ghostPlan = _solver.SolveBuildPlan(cProto, includeGhostStart: true, desiredEntityProtoId: task.Item.EntityPrototypeId)
                                    ?? _solver.SolveBuildPlan(ghostUid.Value, task.Item.EntityPrototypeId);
                    if (ghostPlan != null && ghostPlan.Steps.Count > 0)
                    {
                        StartJob(ghostUid.Value, ghostPlan);
                        return;
                    }
                }
            }
        }
    }

    private BlueprintZPhase GetEffectiveBlueprintPhase(BlueprintItemEntry item)
    {
        if (IsAtmosPipeContraption(item))
            return BlueprintZPhase.Phase1_UnderfloorUtilities;

        return item.Phase;
    }

    private int GetEffectiveBlueprintSubPriority(BlueprintItemEntry item)
    {
        var idLower = (item.EntityPrototypeId ?? string.Empty).ToLowerInvariant();
        if (idLower.Contains("cablehv") || idLower.Contains("cablemv") || idLower.Contains("cableapc"))
            return 0;

        // Filters, mixers, pumps, connector ports, valves, vents, scrubbers, injectors, and disposal trunks
        // MUST be built and anchored on the tile BEFORE overlapping pipes (GasPipe / DisposalPipe).
        if (IsAtmosPipeContraption(item))
            return 1;

        if (idLower.Contains("gaspipe") || idLower.Contains("disposalpipe"))
            return 2;

        return 3 + item.LayerOrder;
    }

    private bool IsAtmosPipeContraption(BlueprintItemEntry item)
    {
        var idLower = (item.EntityPrototypeId ?? string.Empty).ToLowerInvariant();
        var recipeLower = (item.ConstructionPrototypeId ?? string.Empty).ToLowerInvariant();

        if (idLower.Contains("gaspipe") || idLower.Contains("disposalpipe"))
            return false;

        string[] contraptionKeywords =
        {
            "gasfilter",
            "gasmixer",
            "gasport",
            "connectorport",
            "volumepump",
            "pressurepump",
            "passivegate",
            "gasvalve",
            "signalvalve",
            "pneumaticvalve",
            "passivevent",
            "ventpump",
            "ventscrubber",
            "outletinjector",
            "thermomachine",
            "heatexchanger",
            "gasradiator",
            "gasrecycler",
            "canisterport",
            "gastagger",
            "disposaltrunk",
            "disposalrouter",
            "disposaljunction",
            "disposaltagger"
        };

        foreach (var kw in contraptionKeywords)
        {
            if (idLower.Contains(kw, StringComparison.Ordinal) ||
                recipeLower.Contains(kw, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var recipeId = item.ConstructionPrototypeId;
        if (string.IsNullOrEmpty(recipeId) || !_protoMan.HasIndex<ConstructionPrototype>(recipeId))
            recipeId = _solver.ResolveConstructionPrototypeForEntity(item.EntityPrototypeId);

        if (!string.IsNullOrEmpty(recipeId) &&
            _protoMan.TryIndex<ConstructionPrototype>(recipeId, out var cProto))
        {
            var graphId = GetStringMember(cProto, "Graph") ?? string.Empty;
            if (graphId.Equals("GasBinary", StringComparison.OrdinalIgnoreCase) ||
                graphId.Equals("GasTrinary", StringComparison.OrdinalIgnoreCase) ||
                graphId.Equals("GasUnary", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsBlueprintItemAlreadySatisfied(MapCoordinates tileMapCoords, BlueprintItemEntry item, Direction expectedDir)
    {
        if (string.IsNullOrEmpty(item.EntityPrototypeId))
            return false;

        var desiredBaseId = _solver.NormalizeBaseEntityPrototypeId(item.EntityPrototypeId);
        int desiredLayer = _solver.GetDesiredPipeLayer(item.EntityPrototypeId, item.ConstructionPrototypeId);

        var entities = _lookup.GetEntitiesInRange(tileMapCoords, 0.35f, LookupFlags.Uncontained);
        foreach (var uid in entities)
        {
            if (!Exists(uid) || Deleted(uid) || HasGhostComponent(uid))
                continue;

            var protoId = MetaData(uid).EntityPrototype?.ID;
            if (string.IsNullOrEmpty(protoId))
                continue;

            var entBaseId = _solver.NormalizeBaseEntityPrototypeId(protoId);
            if (!string.Equals(protoId, item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(entBaseId, desiredBaseId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var xform = Transform(uid);
            if (!xform.Anchored)
                continue;

            if (xform.LocalRotation.GetCardinalDir() != expectedDir)
                continue;

            int actualLayer = _solver.DetectEntityPipeLayer(uid, out _);
            if (actualLayer == desiredLayer)
                return true;
        }

        return false;
    }

    private EntityUid? FindMatchingFrameOrGhostOnTile(
        MapCoordinates tileMapCoords,
        BlueprintItemEntry item,
        Direction expectedDir,
        string? expectedRecipeId)
    {
        var entities = _lookup.GetEntitiesInRange(tileMapCoords, 0.35f, LookupFlags.Uncontained).ToList();
        EntityUid? matchedGhost = null;

        foreach (var uid in entities)
        {
            if (!Exists(uid) || Deleted(uid))
                continue;

            if (HasGhostComponent(uid))
            {
                var ghostComp = FindComponentByName(uid, "ConstructionGhostComponent");
                var ghostProto = GetMemberValue(ghostComp, "Prototype") as ConstructionPrototype;
                var ghostProtoId = ghostProto?.ID ?? GetStringMember(ghostComp, "Prototype");
                var ghostDir = Transform(uid).LocalRotation.GetCardinalDir();

                if (!string.IsNullOrEmpty(expectedRecipeId) &&
                    string.Equals(ghostProtoId, expectedRecipeId, StringComparison.OrdinalIgnoreCase) &&
                    ghostDir == expectedDir &&
                    matchedGhost == null)
                {
                    matchedGhost = uid;
                }
                else
                {
                    // Clear mismatched ghost so it doesn't block multi-layer placement on this tile
                    ClearGhostEntity(uid);
                }
                continue;
            }

            if (IsGhostOrUnfinishedFrame(uid) &&
                _solver.TryResolveGraphAndNode(uid, out var frameGraph, out _, out _) &&
                frameGraph != null)
            {
                if (!string.IsNullOrEmpty(expectedRecipeId) &&
                    _protoMan.TryIndex<ConstructionPrototype>(expectedRecipeId, out var cProto))
                {
                    var expectedGraphId = GetStringMember(cProto, "Graph");
                    if (!string.Equals(frameGraph.ID, expectedGraphId, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                return uid;
            }
        }

        return matchedGhost;
    }

    private void ClearGhostsOnTile(MapCoordinates tileMapCoords)
    {
        var entities = _lookup.GetEntitiesInRange(tileMapCoords, 0.35f, LookupFlags.Uncontained).ToList();
        foreach (var uid in entities)
        {
            if (Exists(uid) && HasGhostComponent(uid))
                ClearGhostEntity(uid);
        }
    }

    private void ClearGhostEntity(EntityUid ghostUid)
    {
        if (!Exists(ghostUid) || !HasGhostComponent(ghostUid))
            return;

        try
        {
            var ghostComp = FindComponentByName(ghostUid, "ConstructionGhostComponent");
            int ghostId = ghostComp != null && GetMemberValue(ghostComp, "GhostId") is int gid
                ? gid
                : ghostUid.GetHashCode();
            _clientConstruction.ClearGhost(ghostId);
        }
        catch
        {
            // Ignore if ClearGhost fails
        }

        if (Exists(ghostUid) && IsClientSide(ghostUid))
        {
            EntityManager.QueueDeleteEntity(ghostUid);
        }
    }

    private bool TryExecuteDirectStackBlueprintItem(BlueprintItemEntry item, EntityCoordinates tileCoords)
    {
        var idLower = (item.EntityPrototypeId ?? string.Empty).ToLowerInvariant();
        string? stackProtoOrType = null;
        string? matId = null;

        if (idLower.Contains("cablehv"))
        {
            stackProtoOrType = "CableHVStack";
            matId = "CableHV";
        }
        else if (idLower.Contains("cablemv"))
        {
            stackProtoOrType = "CableMVStack";
            matId = "CableMV";
        }
        else if (idLower.Contains("cableapc"))
        {
            stackProtoOrType = "CableApcExtensionStack";
            matId = "Cable";
        }
        else if (idLower.Contains("floortile") || idLower == "tileitemsteel")
        {
            stackProtoOrType = "FloorTileItemSteel";
            matId = "FloorTile";
        }

        if (string.IsNullOrEmpty(stackProtoOrType) || string.IsNullOrEmpty(matId))
            return false;

        bool equipped = _itemUtil.TryEquipItem(stackProtoOrType, 1) ||
                        _itemUtil.TryEquipByPredicate(
                            uid => MatchesMaterialOrStack(uid, matId),
                            $"DirectStack:{stackProtoOrType}",
                            1) ||
                        (_player.LocalEntity is { } player &&
                         TryEquipFromDeepInventoryOrVicinity(player, uid => MatchesMaterialOrStack(uid, matId), 1));

        if (!equipped)
            return false;

        _itemUtil.EnqueueClickCoordinates(tileCoords);
        return true;
    }

    private void ExecuteCurrentStep(EntityUid player, float frameTime)
    {
        if (_activePlan == null)
            return;

        if (_stepIndex >= _activePlan.Steps.Count)
        {
            _log.Info("AutoConstruct plan completed.");
            FinishCurrentPlan();
            return;
        }

        // Track target across entity replacements (e.g. Ghost -> Girder -> WallSolid, Console -> ComputerFrame, or WallSolid -> Girder)
        if (!Exists(_currentTarget) || Deleted(_currentTarget))
        {
            if (TryRelocateTargetEntity(out var replacement))
            {
                _currentTarget = replacement;
                _targetCoordinates = Transform(replacement).Coordinates;
                _targetMapCoords = _transform.GetMapCoordinates(replacement);
                _rescanTimer = 0f;
                _stepRetryCount = 0;
        _equipRetryCount = 0;

                if (_phase == StepExecutionPhase.WaitForCompletion)
                {
                    AdvanceStepOnEntityReplacement();
                }

                if (_activePlan == null || _stepIndex >= _activePlan.Steps.Count)
                {
                    _log.Info("AutoConstruct plan completed after entity replacement.");
                    FinishCurrentPlan();
                    return;
                }

                _phase = StepExecutionPhase.PrepareAndEquip;
                _waitTimer = SettleDelay;
                return;
            }
            else
            {
                // If deconstructing to "start" / "destroyed" and the entity was deleted on the final step, we're done!
                if (_activePlan.IsDeconstruct && _stepIndex >= _activePlan.Steps.Count - 1)
                {
                    _log.Info("Target deconstructed and deleted.");
                    FinishCurrentPlan();
                    return;
                }

                _rescanTimer += frameTime;
                if (_rescanTimer >= TargetRescanTimeout)
                {
                    _log.Warning("Target entity disappeared and no replacement spawned within timeout; stopping job.");
                    FinishCurrentPlan();
                }
                return;
            }
        }
        else
        {
            _targetCoordinates = Transform(_currentTarget).Coordinates;
            _targetMapCoords = _transform.GetMapCoordinates(_currentTarget);
        }

        var step = _activePlan.Steps[_stepIndex];

        // Skip redundant door-weld step if door is already welded shut
        if (step.Kind == ConstructionActionKind.UseTool &&
            step.Description.StartsWith("Weld door shut", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            _wiresSolver.IsDoorWelded(_currentTarget))
        {
            _stepIndex++;
            _phase = StepExecutionPhase.PrepareAndEquip;
            return;
        }

        // Skip redundant wire-panel open step if panel is already open
        if (step.Kind == ConstructionActionKind.UseTool &&
            step.Description.StartsWith("Open wire panel", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            _wiresSolver.IsWirePanelOpen(_currentTarget))
        {
            _stepIndex++;
            _phase = StepExecutionPhase.PrepareAndEquip;
            return;
        }

        // Skip redundant board-pry step on MachineFrame if no board is inside
        if (step.Kind == ConstructionActionKind.UseTool &&
            step.Description.StartsWith("Pry out board", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            !_solver.HasMachineFrameBoard(_currentTarget))
        {
            _stepIndex++;
            _phase = StepExecutionPhase.PrepareAndEquip;
            return;
        }

        // Delegate BUI wire-cutting step (or Cutting step when open wire panel still has uncut BUI wires) to RobustaWiresSolver
        if (step.Kind == ConstructionActionKind.CutWires ||
            (step.Kind == ConstructionActionKind.UseTool &&
             string.Equals(step.PrototypeOrQualityOrTag, "Cutting", StringComparison.OrdinalIgnoreCase) &&
             Exists(_currentTarget) &&
             _wiresSolver.HasOpenWiresPanelWithUncutBuiWires(_currentTarget)))
        {
            ExecuteDelegatedCutWiresStep(step);
            return;
        }

        // Skip redundant anchor/unanchor step if entity is already in the desired anchored state
        if (step.Kind == ConstructionActionKind.UseTool &&
            string.Equals(step.PrototypeOrQualityOrTag, "Anchoring", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget))
        {
            bool isAnchored = Transform(_currentTarget).Anchored;
            if (step.Description.StartsWith("Unanchor", StringComparison.OrdinalIgnoreCase) && !isAnchored)
            {
                _stepIndex++;
                _phase = StepExecutionPhase.PrepareAndEquip;
                return;
            }
            if (step.Description.StartsWith("Anchor frame", StringComparison.OrdinalIgnoreCase) && isAnchored)
            {
                _stepIndex++;
                _phase = StepExecutionPhase.PrepareAndEquip;
                return;
            }
        }

        // Skip redundant pipe-layer screwdriver step if entity is already on that pipe layer
        if (TryGetStepTargetPipeLayer(step, out int desiredStepLayer) &&
            Exists(_currentTarget) &&
            _solver.DetectEntityPipeLayer(_currentTarget, out _) == desiredStepLayer)
        {
            _stepIndex++;
            _phase = StepExecutionPhase.PrepareAndEquip;
            return;
        }

        switch (_phase)
        {
            case StepExecutionPhase.PrepareAndEquip:
            {
                if (!PrepareAndEquipForStep(player, step))
                {
                    if (_equipRetryCount == 0 && TryOpenWornStorageSlots(player))
                    {
                        _equipRetryCount = 1;
                        _waitTimer = 0.35f;
                        return;
                    }

                    _log.Warning($"Failed to equip required item for step [{_stepIndex + 1}/{_activePlan.Steps.Count}]: {step.Description}");
                    FinishCurrentPlan();
                    return;
                }

                _equipRetryCount = 0;

                if (step.Kind == ConstructionActionKind.UseTool &&
                    string.Equals(step.PrototypeOrQualityOrTag, "Welding", StringComparison.OrdinalIgnoreCase))
                {
                    _welderIgniteRetries = 0;
                    _phase = StepExecutionPhase.EnsureWelderLit;
                }
                else
                {
                    _phase = StepExecutionPhase.PerformInteraction;
                }
                break;
            }

            case StepExecutionPhase.EnsureWelderLit:
            {
                var held = _hands.GetActiveItem(player);
                if (held == null || !Exists(held.Value))
                {
                    _phase = StepExecutionPhase.PrepareAndEquip;
                    return;
                }

                if (RequiresWelderIgnition(held.Value))
                {
                    if (IsWelderLit(held.Value))
                    {
                        _litWelderUid = held.Value;
                        _welderIgniteRetries = 0;
                        _phase = StepExecutionPhase.PerformInteraction;
                        break;
                    }

                    _welderIgniteRetries++;
                    if (_welderIgniteRetries > 6)
                    {
                        _log.Warning($"Could not ignite welder {ToPrettyString(held.Value)} for step '{step.Description}'; stopping job.");
                        FinishCurrentPlan();
                        return;
                    }

                    RaisePredictiveEvent(new RequestUseInHandEvent());
                    _litWelderUid = held.Value;
                    _waitTimer = 0.35f;
                    return;
                }

                _phase = StepExecutionPhase.PerformInteraction;
                break;
            }

            case StepExecutionPhase.PerformInteraction:
            {
                _sawDoAfterForStep = false;
                _doAfterCancelledForStep = false;
                _stepWaitElapsed = 0f;
                _postDoAfterTimer = 0f;

                if (step.Kind == ConstructionActionKind.StartGhostConstruction)
                {
                    if (HasGhostComponent(_currentTarget))
                    {
                        StartGhostConstruction(_currentTarget);
                    }
                    _waitTimer = SettleDelay;
                    _phase = StepExecutionPhase.WaitForCompletion;
                    return;
                }

                _stepStartTarget = _currentTarget;
                _stepStartNode = _solver.TryResolveGraphAndNode(_currentTarget, out _, out var sNode, out _) && sNode != null
                    ? sNode.Name
                    : null;
                var activeHeldAtStart = _hands.GetActiveItem(player);
                _stepStartHeldItem = activeHeldAtStart;
                _stepStartHeldStackCount = activeHeldAtStart.HasValue && TryComp<StackComponent>(activeHeldAtStart.Value, out var startStack)
                    ? startStack.Count
                    : (activeHeldAtStart.HasValue ? 1 : 0);

                if (step.Kind == ConstructionActionKind.UseTool &&
                    step.Description.StartsWith("Weld door shut", StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget) &&
                    _wiresSolver.IsDoorOpen(_currentTarget))
                {
                    // Close the open door first; WaitForCompletion will re-trigger welding once closed
                    _itemUtil.EnqueueClickEntity(_currentTarget);
                    _waitTimer = 0.6f;
                    _phase = StepExecutionPhase.WaitForCompletion;
                    return;
                }

                _itemUtil.EnqueueClickEntity(_currentTarget);
                _waitTimer = Math.Max(SettleDelay, step.DoAfterDelay > 0f ? 0.35f : 0.2f);
                _phase = StepExecutionPhase.WaitForCompletion;
                break;
            }

            case StepExecutionPhase.WaitForCompletion:
            {
                // If we started a ghost construction, wait until the ghost is replaced by the real entity
                if (step.Kind == ConstructionActionKind.StartGhostConstruction)
                {
                    if (TryRelocateNonGhostTarget(out var spawnedStructure))
                    {
                        _currentTarget = spawnedStructure;
                        _targetCoordinates = Transform(spawnedStructure).Coordinates;
                        _targetMapCoords = _transform.GetMapCoordinates(spawnedStructure);
                        _rescanTimer = 0f;
                        _stepRetryCount = 0;
        _equipRetryCount = 0;
                        AdvancePastGhostStartSteps();
                        _phase = StepExecutionPhase.PrepareAndEquip;
                        return;
                    }

                    _rescanTimer += frameTime;
                    if (!_sawDoAfterForStep && _rescanTimer >= 0.75f)
                    {
                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        if (_stepRetryCount <= 3)
                        {
                            _phase = StepExecutionPhase.PrepareAndEquip;
                            return;
                        }
                    }

                    if (_rescanTimer >= TargetRescanTimeout || _stepRetryCount > 3)
                    {
                        _log.Warning("Timed out waiting for construction ghost to spawn structure.");
                        FinishCurrentPlan();
                    }
                    return;
                }

                // For steps with a DoAfter delay (e.g. Welding, Wrenching, Prying, Screwing), ensure the DoAfter
                // actually started and finished on the server before swapping tools or advancing to the next step.
                if (step.DoAfterDelay > 0f)
                {
                    _stepWaitElapsed += frameTime;

                    if (_doAfterCancelledForStep)
                    {
                        _log.Warning($"DoAfter cancelled during step '{step.Description}'; stopping job.");
                        FinishCurrentPlan(cancelled: true);
                        return;
                    }

                    if (!_sawDoAfterForStep)
                    {
                        bool stepAlreadyProgressed = !Exists(_currentTarget) ||
                                                     Deleted(_currentTarget) ||
                                                     _currentTarget != _stepStartTarget;

                        if (!stepAlreadyProgressed &&
                            step.Kind == ConstructionActionKind.UseTool &&
                            string.Equals(step.PrototypeOrQualityOrTag, "Anchoring", StringComparison.OrdinalIgnoreCase) &&
                            Exists(_currentTarget))
                        {
                            bool isAnchored = Transform(_currentTarget).Anchored;
                            bool wantsUnanchor = step.Description.StartsWith("Unanchor", StringComparison.OrdinalIgnoreCase);
                            bool wantsAnchor = step.Description.StartsWith("Anchor frame", StringComparison.OrdinalIgnoreCase);
                            if ((wantsUnanchor && !isAnchored) || (wantsAnchor && isAnchored))
                                stepAlreadyProgressed = true;
                        }

                        if (!stepAlreadyProgressed &&
                            step.Description.StartsWith("Open wire panel", StringComparison.OrdinalIgnoreCase) &&
                            Exists(_currentTarget) &&
                            _wiresSolver.IsWirePanelOpen(_currentTarget))
                        {
                            stepAlreadyProgressed = true;
                        }

                        if (!stepAlreadyProgressed &&
                            step.Description.StartsWith("Weld door shut", StringComparison.OrdinalIgnoreCase) &&
                            Exists(_currentTarget) &&
                            _wiresSolver.IsDoorWelded(_currentTarget))
                        {
                            stepAlreadyProgressed = true;
                        }

                        if (!stepAlreadyProgressed &&
                            TryGetStepTargetPipeLayer(step, out int checkPipeLayer))
                        {
                            if (Exists(_currentTarget) &&
                                _solver.DetectEntityPipeLayer(_currentTarget, out _) == checkPipeLayer)
                            {
                                stepAlreadyProgressed = true;
                            }
                            else if (TryRelocateReplacementEntity(_stepStartTarget, out var repPipeEnt) &&
                                     _solver.DetectEntityPipeLayer(repPipeEnt, out _) == checkPipeLayer)
                            {
                                _currentTarget = repPipeEnt;
                                _targetCoordinates = Transform(repPipeEnt).Coordinates;
                                _targetMapCoords = _transform.GetMapCoordinates(repPipeEnt);
                                stepAlreadyProgressed = true;
                            }
                        }

                        if (!stepAlreadyProgressed &&
                            Exists(_currentTarget) &&
                            _solver.TryResolveGraphAndNode(_currentTarget, out _, out var checkNode, out _) &&
                            checkNode != null &&
                            !string.IsNullOrEmpty(step.ExpectedNextNode) &&
                            !string.Equals(step.ExpectedNextNode, _stepStartNode, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(checkNode.Name, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase))
                        {
                            stepAlreadyProgressed = true;
                        }

                        if (!stepAlreadyProgressed && _stepWaitElapsed < Math.Min(0.85f, step.DoAfterDelay + 0.4f))
                        {
                            return;
                        }

                        if (!stepAlreadyProgressed && step.DoAfterDelay >= 0.45f)
                        {
                            _stepRetryCount++;
                            if (_stepRetryCount <= 3)
                            {
                                _rescanTimer = 0f;
                                _phase = StepExecutionPhase.PrepareAndEquip;
                                return;
                            }

                            _log.Warning($"Step '{step.Description}' never started its DoAfter ({step.DoAfterDelay:0.0}s) after retries; stopping job.");
                            FinishCurrentPlan();
                            return;
                        }
                    }
                    else
                    {
                        // Give server-side DoAfter completion and node/entity transition a brief settle buffer
                        // after the client DoAfter finishes so tool swapping never cancels the server's final tick.
                        _postDoAfterTimer += frameTime;
                        if (_postDoAfterTimer < 0.25f)
                            return;
                    }
                }

                // Verify DoorWelded transitions actually took effect before advancing
                if (step.Kind == ConstructionActionKind.UseTool &&
                    step.Description.StartsWith("Weld door shut", StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget) &&
                    !_wiresSolver.IsDoorWelded(_currentTarget))
                {
                    _rescanTimer += frameTime;
                    if (_rescanTimer >= 0.7f)
                    {
                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        if (_stepRetryCount <= 3)
                        {
                            _phase = StepExecutionPhase.PrepareAndEquip;
                            return;
                        }

                        _log.Warning($"Step '{step.Description}' did not weld door shut after retries; stopping job.");
                        FinishCurrentPlan();
                    }
                    return;
                }

                // Verify WirePanel open transitions actually took effect before advancing
                if (step.Kind == ConstructionActionKind.UseTool &&
                    step.Description.StartsWith("Open wire panel", StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget) &&
                    !_wiresSolver.IsWirePanelOpen(_currentTarget))
                {
                    _rescanTimer += frameTime;
                    if (_rescanTimer >= 0.6f)
                    {
                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        if (_stepRetryCount <= 3)
                        {
                            _phase = StepExecutionPhase.PrepareAndEquip;
                            return;
                        }

                        _log.Warning($"Step '{step.Description}' did not open wire panel after retries; stopping job.");
                        FinishCurrentPlan();
                    }
                    return;
                }

                // Verify MachineFrame board/parts pry-out actually took effect before cutting wires
                if (step.Kind == ConstructionActionKind.UseTool &&
                    step.Description.StartsWith("Pry out board", StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget) &&
                    _solver.HasMachineFrameBoard(_currentTarget))
                {
                    _rescanTimer += frameTime;
                    if (_rescanTimer >= 0.65f)
                    {
                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        if (_stepRetryCount <= 3)
                        {
                            _phase = StepExecutionPhase.PrepareAndEquip;
                            return;
                        }

                        _log.Warning($"Step '{step.Description}' did not eject board from machine frame after retries; stopping job.");
                        FinishCurrentPlan();
                    }
                    return;
                }

                // Verify item/material insertions actually consumed or inserted the held item before stowing/advancing
                if (step.Kind is ConstructionActionKind.InsertPrototype
                              or ConstructionActionKind.InsertComponent
                              or ConstructionActionKind.InsertTag
                              or ConstructionActionKind.AddMaterial &&
                    _stepStartHeldItem.HasValue &&
                    Exists(_stepStartHeldItem.Value) &&
                    _hands.GetActiveItem(player) == _stepStartHeldItem)
                {
                    int curCount = TryComp<StackComponent>(_stepStartHeldItem.Value, out var curSt) ? curSt.Count : 1;
                    bool consumed = curCount < _stepStartHeldStackCount;
                    if (!consumed &&
                        (string.Equals(step.PrototypeOrQualityOrTag, "MachineBoard", StringComparison.OrdinalIgnoreCase) ||
                         _solver.IsMachineBoardEntity(_stepStartHeldItem.Value)) &&
                        Exists(_currentTarget) &&
                        _solver.HasMachineFrameBoard(_currentTarget))
                    {
                        consumed = true;
                    }

                    if (!consumed)
                    {
                        _rescanTimer += frameTime;
                        if (_rescanTimer >= 0.55f)
                        {
                            _rescanTimer = 0f;
                            _stepRetryCount++;
                            if (_stepRetryCount <= 2)
                            {
                                _phase = StepExecutionPhase.PrepareAndEquip;
                                return;
                            }

                            // If inserting a part/material into a MachineFrame that already has its board,
                            // that specific requirement slot may already be satisfied; advance to the next step.
                            if (string.Equals(_activePlan.GraphId, "Machine", StringComparison.OrdinalIgnoreCase) &&
                                Exists(_currentTarget) &&
                                _solver.HasMachineFrameBoard(_currentTarget) &&
                                !_solver.IsMachineBoardEntity(_stepStartHeldItem.Value))
                            {
                                _stepIndex++;
                                _stepRetryCount = 0;
        _equipRetryCount = 0;
                                _phase = StepExecutionPhase.PrepareAndEquip;
                                return;
                            }

                            _log.Warning($"Step '{step.Description}' did not consume held item after retries; stopping job.");
                            FinishCurrentPlan();
                        }
                        return;
                    }
                }

                // Verify Unanchor / Anchor transitions actually took effect before advancing
                if (step.Kind == ConstructionActionKind.UseTool &&
                    string.Equals(step.PrototypeOrQualityOrTag, "Anchoring", StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget))
                {
                    bool isAnchored = Transform(_currentTarget).Anchored;
                    bool wantsUnanchor = step.Description.StartsWith("Unanchor", StringComparison.OrdinalIgnoreCase);
                    bool wantsAnchor = step.Description.StartsWith("Anchor frame", StringComparison.OrdinalIgnoreCase);

                    if ((wantsUnanchor && isAnchored) || (wantsAnchor && !isAnchored))
                    {
                        _rescanTimer += frameTime;
                        if (_rescanTimer >= 0.7f)
                        {
                            // If anchoring on Primary failed and the next step(s) adjust pipe layer via Screwdriver,
                            // swap the pipe-layer Screwdriver step(s) before Anchor frame so it switches layer while unanchored first.
                            if (wantsAnchor && !isAnchored &&
                                _stepIndex + 1 < _activePlan.Steps.Count &&
                                TryGetStepTargetPipeLayer(_activePlan.Steps[_stepIndex + 1], out _))
                            {
                                var anchorStep = _activePlan.Steps[_stepIndex];
                                int insertIdx = _stepIndex;
                                while (insertIdx + 1 < _activePlan.Steps.Count &&
                                       TryGetStepTargetPipeLayer(_activePlan.Steps[insertIdx + 1], out _))
                                {
                                    _activePlan.Steps[insertIdx] = _activePlan.Steps[insertIdx + 1];
                                    insertIdx++;
                                }
                                _activePlan.Steps[insertIdx] = anchorStep;
                                _rescanTimer = 0f;
                                _stepRetryCount = 0;
        _equipRetryCount = 0;
                                _phase = StepExecutionPhase.PrepareAndEquip;
                                return;
                            }

                            _rescanTimer = 0f;
                            _stepRetryCount++;
                            if (_stepRetryCount <= 3)
                            {
                                _phase = StepExecutionPhase.PrepareAndEquip;
                                return;
                            }

                            _log.Warning($"Step '{step.Description}' did not change anchored state after retries; stopping job.");
                            FinishCurrentPlan();
                        }
                        return;
                    }
                }

                // Verify pipe-layer Screwdriver transitions (both in-place and entity-replacement)
                if (TryGetStepTargetPipeLayer(step, out int verifyPipeLayer))
                {
                    if ((!Exists(_currentTarget) || Deleted(_currentTarget)) &&
                        TryRelocateTargetEntity(out var relocatedPipeEnt))
                    {
                        _currentTarget = relocatedPipeEnt;
                        _targetCoordinates = Transform(relocatedPipeEnt).Coordinates;
                        _targetMapCoords = _transform.GetMapCoordinates(relocatedPipeEnt);
                    }
                    else if (Exists(_currentTarget) &&
                             _currentTarget == _stepStartTarget &&
                             _solver.DetectEntityPipeLayer(_currentTarget, out _) != verifyPipeLayer &&
                             TryRelocateReplacementEntity(_stepStartTarget, out var replacedPipeEnt))
                    {
                        _currentTarget = replacedPipeEnt;
                        _targetCoordinates = Transform(replacedPipeEnt).Coordinates;
                        _targetMapCoords = _transform.GetMapCoordinates(replacedPipeEnt);
                    }

                    if (Exists(_currentTarget) &&
                        _solver.DetectEntityPipeLayer(_currentTarget, out _) != verifyPipeLayer)
                    {
                        _rescanTimer += frameTime;
                        if (_rescanTimer >= 0.65f)
                        {
                            _rescanTimer = 0f;
                            _stepRetryCount++;
                            if (_stepRetryCount <= 3)
                            {
                                _phase = StepExecutionPhase.PrepareAndEquip;
                                return;
                            }

                            _log.Warning($"Step '{step.Description}' did not change pipe layer after retries; advancing.");
                        }
                        else
                        {
                            return;
                        }
                    }
                }

                // If this step is the final step of an edge that replaces/deletes the entity
                // (e.g. Console -> ComputerFrame, Machine -> MachineFrame, Wall -> Girder, or -> start),
                // wait for the entity replacement before advancing.
                bool isFinalStepOfEdge =
                    !string.IsNullOrEmpty(step.ExpectedNextNode) &&
                    (_stepIndex + 1 >= _activePlan.Steps.Count ||
                     !string.Equals(_activePlan.Steps[_stepIndex + 1].ExpectedNextNode, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase));

                bool expectsEntityReplacement =
                    isFinalStepOfEdge &&
                    Exists(_currentTarget) &&
                    _solver.DoesEdgeReplaceOrDeleteEntity(_currentTarget, _activePlan.GraphId, step.ExpectedNextNode);

                if (expectsEntityReplacement && Exists(_currentTarget) && _currentTarget == _stepStartTarget)
                {
                    if (TryRelocateReplacementEntity(_stepStartTarget, out var spawnedReplacement))
                    {
                        _currentTarget = spawnedReplacement;
                        _targetCoordinates = Transform(spawnedReplacement).Coordinates;
                        _targetMapCoords = _transform.GetMapCoordinates(spawnedReplacement);
                        _stepIndex++;
                        _rescanTimer = 0f;
                        _stepRetryCount = 0;
        _equipRetryCount = 0;
                        _phase = StepExecutionPhase.PrepareAndEquip;
                        return;
                    }

                    _rescanTimer += frameTime;
                    if (_rescanTimer >= 0.85f)
                    {
                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        if (_stepRetryCount <= 3)
                        {
                            _phase = StepExecutionPhase.PrepareAndEquip;
                            return;
                        }

                        _log.Warning($"Step '{step.Description}' did not transition to node '{step.ExpectedNextNode}' after retries; stopping job.");
                        FinishCurrentPlan();
                    }
                    return;
                }

                // If this step transitions an in-place frame node (e.g. MachineFrame missingWires <-> machineFrame,
                // or ComputerFrame stages 0..4) without replacing the entity, verify the node changed before advancing.
                if (isFinalStepOfEdge &&
                    !expectsEntityReplacement &&
                    !string.IsNullOrEmpty(step.ExpectedNextNode) &&
                    !string.Equals(step.ExpectedNextNode, _stepStartNode, StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget) &&
                    _solver.CanVerifyInPlaceNodeTransition(_currentTarget, _activePlan.GraphId) &&
                    _solver.TryResolveGraphAndNode(_currentTarget, out _, out var currentFrameNode, out _) &&
                    currentFrameNode != null &&
                    !string.Equals(currentFrameNode.Name, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase))
                {
                    _rescanTimer += frameTime;
                    if (_rescanTimer >= 0.65f)
                    {
                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        if (_stepRetryCount <= 3)
                        {
                            _phase = StepExecutionPhase.PrepareAndEquip;
                            return;
                        }

                        _log.Warning($"Step '{step.Description}' did not reach frame node '{step.ExpectedNextNode}' after retries; stopping job.");
                        FinishCurrentPlan();
                    }
                    return;
                }

                _stepIndex++;
                _rescanTimer = 0f;
                _stepRetryCount = 0;
        _equipRetryCount = 0;
                _phase = StepExecutionPhase.PrepareAndEquip;
                break;
            }
        }
    }

    private void AdvancePastGhostStartSteps()
    {
        if (_activePlan == null || _stepIndex >= _activePlan.Steps.Count)
            return;

        var ghostStep = _activePlan.Steps[_stepIndex];
        var firstNode = ghostStep.ExpectedNextNode;
        _stepIndex++;

        if (string.IsNullOrEmpty(firstNode))
            return;

        while (_stepIndex < _activePlan.Steps.Count &&
               string.Equals(_activePlan.Steps[_stepIndex].ExpectedNextNode, firstNode, StringComparison.OrdinalIgnoreCase))
        {
            _stepIndex++;
        }
    }

    private void AdvanceStepOnEntityReplacement()
    {
        if (_activePlan == null || _stepIndex >= _activePlan.Steps.Count)
            return;

        var curStep = _activePlan.Steps[_stepIndex];
        if (curStep.Kind == ConstructionActionKind.StartGhostConstruction)
        {
            AdvancePastGhostStartSteps();
            return;
        }

        // Advance past the current step that triggered the entity replacement.
        _stepIndex++;
    }

    private void ExecuteDelegatedCutWiresStep(ConstructionActionStep step)
    {
        if (!Exists(_currentTarget))
            return;

        if (!_solver.HasWiresUserInterface(_currentTarget))
        {
            if (step.Kind == ConstructionActionKind.CutWires)
                _stepIndex++;
            _phase = StepExecutionPhase.PrepareAndEquip;
            return;
        }

        if (!_delegatedWireSolveStarted)
        {
            if (_wiresSolver.AreAllWiresCut(_currentTarget) && !_wiresSolver.IsDoorBolted(_currentTarget))
            {
                if (step.Kind == ConstructionActionKind.CutWires)
                    _stepIndex++;
                _phase = StepExecutionPhase.PrepareAndEquip;
                _waitTimer = SettleDelay;
                return;
            }

            if (!_wiresSolver.TryStartSolve(
                    _currentTarget,
                    RobustaWiresSolver.CutAllIntent,
                    out var failureReason,
                    allowNestedFromConstruction: true))
            {
                _log.Warning($"Could not start wire-cut solve on {ToPrettyString(_currentTarget)}: {failureReason}.");
                FinishCurrentPlan();
                return;
            }

            _delegatedWireSolveStarted = true;
            return;
        }

        if (_wiresSolver.IsSolving)
            return;

        _delegatedWireSolveStarted = false;
        if (_wiresSolver.AreAllWiresCut(_currentTarget) && !_wiresSolver.IsDoorBolted(_currentTarget))
        {
            if (step.Kind == ConstructionActionKind.CutWires)
                _stepIndex++;
            _rescanTimer = 0f;
            _stepRetryCount = 0;
        _equipRetryCount = 0;
            _phase = StepExecutionPhase.PrepareAndEquip;
            _waitTimer = SettleDelay;
            return;
        }

        _stepRetryCount++;
        if (_stepRetryCount <= 2)
            return;

        _log.Warning($"Wire-cut solve on {ToPrettyString(_currentTarget)} did not cut all wires; stopping job.");
        FinishCurrentPlan();
    }

    private bool PrepareAndEquipForStep(EntityUid player, ConstructionActionStep step)
    {
        switch (step.Kind)
        {
            case ConstructionActionKind.StartGhostConstruction:
            {
                // Equip the first required material/item from the next step so the server accepts TryStartStructureConstructionMessage
                if (_activePlan != null && _stepIndex + 1 < _activePlan.Steps.Count)
                {
                    var firstStep = _activePlan.Steps[_stepIndex + 1];
                    if (firstStep.Kind != ConstructionActionKind.StartGhostConstruction)
                        return PrepareAndEquipForStep(player, firstStep);
                }
                return true;
            }

            case ConstructionActionKind.AddMaterial:
            {
                var matId = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(matId))
                    return false;

                // 1. Unified material/stack matcher (handles StackTypeId, prototype Stack, StackPrototype.Spawn, CableCoil/LV/MV/HV cables, etc.)
                if (_itemUtil.TryEquipByPredicate(
                        uid => MatchesMaterialOrStack(uid, matId),
                        $"Material:{matId}",
                        step.Amount))
                {
                    return true;
                }

                // 2. MachinePart / StockPart fallback
                if (_itemUtil.TryEquipByPredicate(
                        uid => MatchesMachinePart(uid, matId),
                        $"MaterialOrPart:{matId}",
                        step.Amount))
                {
                    return true;
                }

                // 3. Deep container (e.g. toolbox/box inside backpack, secondary containers) & 2.5m vicinity fallback
                return TryEquipFromDeepInventoryOrVicinity(
                    player,
                    uid => MatchesMaterialOrStack(uid, matId) || MatchesMachinePart(uid, matId),
                    step.Amount);
            }

            case ConstructionActionKind.UseTool:
            case ConstructionActionKind.CutWires:
            {
                var quality = step.PrototypeOrQualityOrTag ?? "Cutting";
                if (string.IsNullOrEmpty(quality))
                    return false;

                if (_itemUtil.TryEquipByPredicate(
                        uid => _toolSystem.HasQuality(uid, quality),
                        $"Tool:{quality}"))
                {
                    return true;
                }

                return TryEquipFromDeepInventoryOrVicinity(
                    player,
                    uid => _toolSystem.HasQuality(uid, quality),
                    1);
            }

            case ConstructionActionKind.InsertPrototype:
            {
                var protoId = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(protoId))
                    return false;

                if (_itemUtil.TryEquipItem(new EntProtoId(protoId), step.Amount, includeChildren: true))
                    return true;

                if (_itemUtil.TryEquipByPredicate(
                        uid => MatchesPrototypeOrParentId(uid, protoId, allowPrefixMatch: false) || HasValidTag(uid, protoId),
                        $"Prototype:{protoId}",
                        step.Amount))
                {
                    return true;
                }

                return TryEquipFromDeepInventoryOrVicinity(
                    player,
                    uid => MatchesPrototypeOrParentId(uid, protoId, allowPrefixMatch: false) || HasValidTag(uid, protoId),
                    step.Amount);
            }

            case ConstructionActionKind.InsertComponent:
            {
                var compName = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(compName))
                    return false;

                if (string.Equals(compName, "MachineBoard", StringComparison.OrdinalIgnoreCase))
                {
                    return _itemUtil.TryEquipByPredicate(
                               uid => _solver.IsMachineBoardEntity(uid),
                               "Component:MachineBoard",
                               step.Amount) ||
                           TryEquipFromDeepInventoryOrVicinity(
                               player,
                               uid => _solver.IsMachineBoardEntity(uid),
                               step.Amount);
                }

                if (compName.StartsWith("MachinePart:", StringComparison.OrdinalIgnoreCase))
                {
                    var partId = compName["MachinePart:".Length..];
                    return _itemUtil.TryEquipByPredicate(
                               uid => MatchesMachinePart(uid, partId),
                               $"MachinePart:{partId}",
                               step.Amount) ||
                           TryEquipFromDeepInventoryOrVicinity(
                               player,
                               uid => MatchesMachinePart(uid, partId),
                               step.Amount);
                }

                bool MatchesComponentRequirement(EntityUid uid)
                {
                    if (!Exists(uid))
                        return false;

                    if (EntityManager.GetComponents(uid).Any(c =>
                            c.GetType().Name.Equals(compName, StringComparison.OrdinalIgnoreCase) ||
                            c.GetType().Name.Equals(compName + "Component", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }

                    var proto = MetaData(uid).EntityPrototype;
                    if (proto != null && _itemUtil.HasPrototypeComponent(proto, compName))
                        return true;

                    return MatchesTagOrPrototype(uid, compName);
                }

                return _itemUtil.TryEquipByPredicate(
                           MatchesComponentRequirement,
                           $"Component:{compName}",
                           step.Amount) ||
                       TryEquipFromDeepInventoryOrVicinity(
                           player,
                           MatchesComponentRequirement,
                           step.Amount);
            }

            case ConstructionActionKind.InsertTag:
            {
                var tag = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(tag))
                    return false;

                return _itemUtil.TryEquipByPredicate(
                           uid => MatchesTagOrPrototype(uid, tag),
                           $"Tag:{tag}",
                           step.Amount) ||
                       TryEquipFromDeepInventoryOrVicinity(
                           player,
                           uid => MatchesTagOrPrototype(uid, tag),
                           step.Amount);
            }

            default:
                return false;
        }
    }

    private bool HasValidTag(EntityUid uid, string? tag)
    {
        if (!Exists(uid) || string.IsNullOrWhiteSpace(tag))
            return false;

        try
        {
            if (!_protoMan.HasIndex<TagPrototype>(tag))
                return false;

            return _tagSystem.HasTag(uid, tag);
        }
        catch
        {
            return false;
        }
    }

    private bool MatchesTagOrPrototype(EntityUid uid, string tag)
    {
        if (!Exists(uid) || string.IsNullOrWhiteSpace(tag))
            return false;

        if (HasValidTag(uid, tag))
            return true;

        if (MatchesPrototypeOrParentId(uid, tag, allowPrefixMatch: false))
            return true;

        if (EntityManager.GetComponents(uid).Any(c =>
                c.GetType().Name.Equals(tag, StringComparison.OrdinalIgnoreCase) ||
                c.GetType().Name.Equals(tag + "Component", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var proto = MetaData(uid).EntityPrototype;
        if (proto != null && _itemUtil.HasPrototypeComponent(proto, tag))
            return true;

        return false;
    }

    private bool MatchesMaterialOrStack(EntityUid uid, string matId)
    {
        if (!Exists(uid) || string.IsNullOrWhiteSpace(matId))
            return false;

        string? liveStackType = null;
        if (TryComp<StackComponent>(uid, out var stack))
        {
            liveStackType = stack.StackTypeId.Id;
            if (string.IsNullOrEmpty(liveStackType))
                liveStackType = GetStringMember(stack, "StackTypeId") ?? GetStringMember(stack, "StackType");
        }

        if (string.IsNullOrEmpty(liveStackType) && FindComponentByName(uid, "StackComponent") is { } rawStack)
        {
            liveStackType = GetStringMember(rawStack, "StackTypeId") ?? GetStringMember(rawStack, "StackType");
        }

        if (!string.IsNullOrEmpty(liveStackType) &&
            string.Equals(liveStackType, matId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryGetPrototypeStackType(uid, out var protoStackType))
        {
            if (string.Equals(protoStackType, matId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.IsNullOrEmpty(liveStackType))
                liveStackType = protoStackType;
        }

        // Check if StackPrototype's Spawn entity prototype matches this entity or its parent prototypes
        if (_protoMan.TryIndex<StackPrototype>(matId, out var stackProto))
        {
            var spawnProtoId = GetStringMember(stackProto, "Spawn");
            if (!string.IsNullOrEmpty(spawnProtoId) &&
                MatchesPrototypeOrParentId(uid, spawnProtoId, allowPrefixMatch: true))
            {
                return true;
            }
        }

        // Handle LV / MV / HV Cable family matching
        bool wantsLvCable = string.Equals(matId, "Cable", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matId, "CableLV", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matId, "CableApc", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matId, "CableApcExtension", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matId, "CableCoil", StringComparison.OrdinalIgnoreCase);

        if (wantsLvCable)
        {
            // Exclude MV/HV cables and non-coil items like cable cuffs, terminals, zipties, bolas
            if (!string.IsNullOrEmpty(liveStackType) &&
                (liveStackType.Contains("MV", StringComparison.OrdinalIgnoreCase) ||
                 liveStackType.Contains("HV", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (MatchesPrototypeOrParentSubstring(uid, "CableMV") ||
                MatchesPrototypeOrParentSubstring(uid, "CableHV") ||
                MatchesPrototypeOrParentSubstring(uid, "CableTerminal") ||
                MatchesPrototypeOrParentSubstring(uid, "Cuffs") ||
                MatchesPrototypeOrParentSubstring(uid, "Restraint") ||
                MatchesPrototypeOrParentSubstring(uid, "Ziptie") ||
                MatchesPrototypeOrParentSubstring(uid, "Bola"))
            {
                return false;
            }

            if (HasValidTag(uid, "CableCoil") ||
                HasValidTag(uid, "Cable") ||
                HasValidTag(uid, "CableLV") ||
                HasValidTag(uid, "CableApc"))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(liveStackType) &&
                (liveStackType.Contains("Cable", StringComparison.OrdinalIgnoreCase) ||
                 liveStackType.Contains("Apc", StringComparison.OrdinalIgnoreCase) ||
                 liveStackType.Contains("LV", StringComparison.OrdinalIgnoreCase) ||
                 liveStackType.Contains("Wire", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (MatchesPrototypeOrParentSubstring(uid, "CableApc") ||
                MatchesPrototypeOrParentSubstring(uid, "CableLV") ||
                MatchesPrototypeOrParentSubstring(uid, "CableStack"))
            {
                return true;
            }

            if (FindComponentByName(uid, "CablePlacerComponent") is { } cablePlacer)
            {
                var cableProto = GetStringMember(cablePlacer, "CablePrototypeId")
                                 ?? GetStringMember(cablePlacer, "CablePrototypeID")
                                 ?? GetStringMember(cablePlacer, "Cable")
                                 ?? string.Empty;
                if (!cableProto.Contains("MV", StringComparison.OrdinalIgnoreCase) &&
                    !cableProto.Contains("HV", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var proto = MetaData(uid).EntityPrototype;
            if (proto != null &&
                _itemUtil.TryGetPrototypeComponent<object>(proto, "CablePlacer", out var protoPlacer) &&
                protoPlacer != null)
            {
                var cableProto = GetStringMember(protoPlacer, "CablePrototypeId")
                                 ?? GetStringMember(protoPlacer, "CablePrototypeID")
                                 ?? GetStringMember(protoPlacer, "Cable")
                                 ?? string.Empty;
                if (!cableProto.Contains("MV", StringComparison.OrdinalIgnoreCase) &&
                    !cableProto.Contains("HV", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        else if (string.Equals(matId, "CableMV", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(matId, "CableMVStack", StringComparison.OrdinalIgnoreCase))
        {
            if ((!string.IsNullOrEmpty(liveStackType) && liveStackType.Contains("CableMV", StringComparison.OrdinalIgnoreCase)) ||
                MatchesPrototypeOrParentSubstring(uid, "CableMV"))
            {
                return true;
            }
        }
        else if (string.Equals(matId, "CableHV", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(matId, "CableHVStack", StringComparison.OrdinalIgnoreCase))
        {
            if ((!string.IsNullOrEmpty(liveStackType) && liveStackType.Contains("CableHV", StringComparison.OrdinalIgnoreCase)) ||
                MatchesPrototypeOrParentSubstring(uid, "CableHV"))
            {
                return true;
            }
        }

        // General sheet/stack prototype naming conventions (e.g. SheetSteel, SheetGlass, MaterialPlasteel)
        if (HasComp<StackComponent>(uid) || !string.IsNullOrEmpty(liveStackType))
        {
            if (MatchesPrototypeOrParentId(uid, matId, allowPrefixMatch: true) ||
                MatchesPrototypeOrParentId(uid, $"Sheet{matId}", allowPrefixMatch: true) ||
                MatchesPrototypeOrParentId(uid, $"Material{matId}", allowPrefixMatch: true) ||
                MatchesPrototypeOrParentId(uid, $"{matId}Stack", allowPrefixMatch: true) ||
                MatchesPrototypeOrParentId(uid, $"Part{matId}", allowPrefixMatch: true))
            {
                return true;
            }
        }

        return HasValidTag(uid, matId);
    }

    private bool TryGetPrototypeStackType(EntityUid uid, out string stackType)
    {
        stackType = string.Empty;
        var proto = MetaData(uid).EntityPrototype;
        if (proto == null)
            return false;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<EntityPrototype>();
        queue.Enqueue(proto);
        visited.Add(proto.ID);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (_itemUtil.TryGetPrototypeComponent<object>(cur, "Stack", out var stackComp) && stackComp != null)
            {
                var st = GetStringMember(stackComp, "StackTypeId") ?? GetStringMember(stackComp, "StackType");
                if (!string.IsNullOrEmpty(st))
                {
                    stackType = st;
                    return true;
                }
            }

            if (cur.Parents != null)
            {
                foreach (var parentId in cur.Parents)
                {
                    if (visited.Add(parentId) && _protoMan.TryIndex<EntityPrototype>(parentId, out var parentProto))
                        queue.Enqueue(parentProto);
                }
            }
        }

        return false;
    }

    private bool MatchesPrototypeOrParentId(EntityUid uid, string targetProtoId, bool allowPrefixMatch)
    {
        if (!Exists(uid) || string.IsNullOrEmpty(targetProtoId))
            return false;

        var proto = MetaData(uid).EntityPrototype;
        if (proto == null)
            return false;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<EntityPrototype>();
        queue.Enqueue(proto);
        visited.Add(proto.ID);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (string.Equals(cur.ID, targetProtoId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (allowPrefixMatch &&
                (cur.ID.StartsWith(targetProtoId, StringComparison.OrdinalIgnoreCase) ||
                 targetProtoId.StartsWith(cur.ID, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (cur.Parents != null)
            {
                foreach (var parentId in cur.Parents)
                {
                    if (string.Equals(parentId, targetProtoId, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (visited.Add(parentId) && _protoMan.TryIndex<EntityPrototype>(parentId, out var parentProto))
                        queue.Enqueue(parentProto);
                }
            }
        }

        return false;
    }

    private bool MatchesPrototypeOrParentSubstring(EntityUid uid, string substring)
    {
        if (!Exists(uid) || string.IsNullOrEmpty(substring))
            return false;

        var proto = MetaData(uid).EntityPrototype;
        if (proto == null)
            return false;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<EntityPrototype>();
        queue.Enqueue(proto);
        visited.Add(proto.ID);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur.ID.Contains(substring, StringComparison.OrdinalIgnoreCase))
                return true;

            if (cur.Parents != null)
            {
                foreach (var parentId in cur.Parents)
                {
                    if (parentId.Contains(substring, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (visited.Add(parentId) && _protoMan.TryIndex<EntityPrototype>(parentId, out var parentProto))
                        queue.Enqueue(parentProto);
                }
            }
        }

        return false;
    }

    private bool TryOpenWornStorageSlots(EntityUid player)
    {
        if (!Exists(player))
            return false;

        bool openedAny = false;
        string[] storageSlots = { "belt", "back", "suitstorage", "outerClothing", "pocket1", "pocket2" };
        foreach (var slotName in storageSlots)
        {
            if (_inventory.TryGetSlotEntity(player, slotName, out var slotEnt) &&
                slotEnt.HasValue &&
                Exists(slotEnt.Value) &&
                HasComp<StorageComponent>(slotEnt.Value))
            {
                RaisePredictiveEvent(new OpenSlotStorageNetworkMessage(slotName));
                openedAny = true;
            }
        }

        return openedAny;
    }

    private bool TryEquipFromDeepInventoryOrVicinity(
        EntityUid player,
        Func<EntityUid, bool> predicate,
        int neededAmount)
    {
        if (!Exists(player) || !TryComp<HandsComponent>(player, out var hands))
            return false;

        int needed = Math.Max(1, neededAmount);

        // 1. Check up to MaxInteractRange (2.5m) via ItemUtilitySystem's vicinity search
        var vicinityMatches = _itemUtil.FindItemsByPredicate(predicate, needed, MaxInteractRange);
        int vicinityTotal = vicinityMatches.Sum(m => m.StackCount);
        if (vicinityMatches.Count > 0 && vicinityTotal >= needed)
        {
            var best = vicinityMatches[0];
            _itemUtil.StowActiveTrackedItem();
            if (best.Source == ItemSource.Storage && best.StorageEntity is { } storageUid)
            {
                if (!string.IsNullOrEmpty(best.SlotOrHand))
                {
                    var slotName = best.SlotOrHand;
                    _itemUtil.EnqueueStep(ItemUtilitySystem.StepDelay, () => RaisePredictiveEvent(new OpenSlotStorageNetworkMessage(slotName)));
                }
                var itemUid = best.Entity;
                _itemUtil.EnqueueStep(ItemUtilitySystem.StepDelay, () =>
                {
                    if (!Exists(itemUid) || !Exists(storageUid))
                        return;
                    EnsureEmptyActiveHandImmediate(player);
                    RaisePredictiveEvent(new StorageInteractWithItemEvent(GetNetEntity(itemUid), GetNetEntity(storageUid)));
                });
                return true;
            }

            if (best.Source == ItemSource.Vicinity)
            {
                _itemUtil.EnqueueClickEntity(best.Entity);
                return true;
            }
        }

        // 2. Deep scan nested storage containers (e.g. toolbox/box inside backpack, secondary containers on worn items, or open containers nearby)
        var deepCandidates = new List<(EntityUid Item, EntityUid? StorageOwner, string? RootSlot, int Count)>();
        var visited = new HashSet<EntityUid>();

        void ScanContainerRecursive(EntityUid containerOwner, string? rootSlot, EntityUid? activeStorageOwner, int depth)
        {
            if (depth > 3 || !Exists(containerOwner))
                return;

            EntityUid? currentStorage = HasComp<StorageComponent>(containerOwner) ? containerOwner : activeStorageOwner;

            if (TryComp<StorageComponent>(containerOwner, out var stComp))
            {
                foreach (var ent in stComp.Container.ContainedEntities)
                {
                    if (!Exists(ent) || !visited.Add(ent))
                        continue;

                    if (predicate(ent))
                    {
                        int count = TryComp<StackComponent>(ent, out var s) ? s.Count : 1;
                        deepCandidates.Add((ent, currentStorage ?? containerOwner, rootSlot, count));
                    }

                    ScanContainerRecursive(ent, rootSlot, currentStorage ?? containerOwner, depth + 1);
                }
            }

            if (TryComp<ContainerManagerComponent>(containerOwner, out var containerMan))
            {
                foreach (var container in _container.GetAllContainers(containerOwner, containerMan))
                {
                    foreach (var ent in container.ContainedEntities)
                    {
                        if (!Exists(ent) || !visited.Add(ent))
                            continue;

                        if (predicate(ent))
                        {
                            int count = TryComp<StackComponent>(ent, out var s) ? s.Count : 1;
                            deepCandidates.Add((ent, currentStorage ?? containerOwner, rootSlot, count));
                        }

                        ScanContainerRecursive(ent, rootSlot, currentStorage ?? containerOwner, depth + 1);
                    }
                }
            }
        }

        // Scan hands recursively
        foreach (var handName in hands.Hands.Keys)
        {
            if (_container.TryGetContainer(player, handName, out var handCont) && handCont.ContainedEntities.Count > 0)
            {
                var heldEnt = handCont.ContainedEntities[0];
                ScanContainerRecursive(heldEnt, null, HasComp<StorageComponent>(heldEnt) ? heldEnt : null, 1);
            }
        }

        // Scan worn slots recursively
        string[] allSlots =
        {
            "belt", "back", "pocket1", "pocket2", "pocket3", "pocket4",
            "suitstorage", "id", "outerClothing", "gloves", "shoes",
            "head", "eyes", "ears", "mask", "neck", "jumpsuit"
        };
        foreach (var slotName in allSlots)
        {
            if (_inventory.TryGetSlotEntity(player, slotName, out var slotEnt) && slotEnt.HasValue)
            {
                ScanContainerRecursive(slotEnt.Value, slotName, HasComp<StorageComponent>(slotEnt.Value) ? slotEnt.Value : null, 1);
            }
        }

        // Also scan open StorageComponent entities within MaxInteractRange (e.g. open toolbox on the floor)
        var playerCoords = _transform.GetMapCoordinates(player);
        if (playerCoords.MapId != MapId.Nullspace)
        {
            foreach (var nearEnt in _lookup.GetEntitiesInRange(playerCoords, MaxInteractRange, LookupFlags.Uncontained))
            {
                if (nearEnt == player || !Exists(nearEnt) || !HasComp<StorageComponent>(nearEnt))
                    continue;

                ScanContainerRecursive(nearEnt, null, nearEnt, 1);
            }
        }

        if (deepCandidates.Count == 0 || deepCandidates.Sum(c => c.Count) < needed)
            return false;

        var chosen = deepCandidates
            .OrderByDescending(c => c.Count >= needed)
            .ThenByDescending(c => c.Count)
            .First();

        _itemUtil.StowActiveTrackedItem();

        if (!string.IsNullOrEmpty(chosen.RootSlot))
        {
            var rootSlot = chosen.RootSlot;
            _itemUtil.EnqueueStep(ItemUtilitySystem.StepDelay, () => RaisePredictiveEvent(new OpenSlotStorageNetworkMessage(rootSlot)));
        }

        if (chosen.StorageOwner is { } storageOwner && Exists(storageOwner))
        {
            var itemToFetch = chosen.Item;
            _itemUtil.EnqueueStep(ItemUtilitySystem.StepDelay, () =>
            {
                if (!Exists(itemToFetch) || !Exists(storageOwner))
                    return;
                EnsureEmptyActiveHandImmediate(player);
                RaisePredictiveEvent(new StorageInteractWithItemEvent(GetNetEntity(itemToFetch), GetNetEntity(storageOwner)));
            });
            return true;
        }

        _itemUtil.EnqueueClickEntity(chosen.Item);
        return true;
    }

    private void EnsureEmptyActiveHandImmediate(EntityUid player)
    {
        if (!TryComp<HandsComponent>(player, out var hands))
            return;

        var activeHand = _hands.GetActiveHand(player);
        if (activeHand != null && _hands.GetActiveItem(player) == null)
            return;

        foreach (var handName in hands.Hands.Keys)
        {
            if (_container.TryGetContainer(player, handName, out var cont) && cont.ContainedEntities.Count == 0)
            {
                RaisePredictiveEvent(new RequestSetHandEvent(handName));
                return;
            }
        }
    }

    private bool MatchesMachinePart(EntityUid uid, string partId)
    {
        if (!Exists(uid) || string.IsNullOrEmpty(partId))
            return false;

        if (MatchesMaterialOrStack(uid, partId))
            return true;

        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "MachinePartComponent", StringComparison.OrdinalIgnoreCase))
            {
                var partVal = GetMemberValue(comp, "Part") ?? GetMemberValue(comp, "PartType");
                var partStr = GetMemberValue(partVal, "Id") as string ?? partVal?.ToString();
                if (string.Equals(partStr, partId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        var proto = MetaData(uid).EntityPrototype;
        if (proto != null)
        {
            if (_itemUtil.TryGetPrototypeComponent<object>(proto, "MachinePart", out var mpComp) && mpComp != null)
            {
                var partVal = GetMemberValue(mpComp, "Part") ?? GetMemberValue(mpComp, "PartType");
                var partStr = GetMemberValue(partVal, "Id") as string ?? partVal?.ToString();
                if (string.Equals(partStr, partId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (proto.ID.Contains(partId, StringComparison.OrdinalIgnoreCase) &&
                proto.ID.Contains("StockPart", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return HasValidTag(uid, partId);
    }

    private void StartGhostConstruction(EntityUid ghostUid)
    {
        try
        {
            _clientConstruction.TryStartConstruction(ghostUid);
            return;
        }
        catch
        {
            // Fallback reflection if signature differs on older fork
        }

        try
        {
            var method = _clientConstruction.GetType().GetMethod(
                "TryStartConstruction",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null)
            {
                var parameters = method.GetParameters();
                var args = new object?[parameters.Length];
                if (parameters.Length > 0)
                    args[0] = ghostUid;
                method.Invoke(_clientConstruction, args);
                return;
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Reflection fallback for TryStartConstruction failed: {ex.Message}");
        }
    }

    private EntityUid? FindTargetAtCursor(EntityUid player, MapCoordinates cursorMapCoords)
    {
        var playerPos = _transform.GetMapCoordinates(player);
        var nearby = _lookup.GetEntitiesInRange(cursorMapCoords, 0.65f, LookupFlags.Uncontained);

        // 1. Check for a ConstructionGhostComponent first
        EntityUid? bestGhost = null;
        float bestGhostDist = float.MaxValue;
        foreach (var uid in nearby)
        {
            if (!Exists(uid) || !HasGhostComponent(uid))
                continue;

            var pos = _transform.GetMapCoordinates(uid);
            if (pos.MapId != playerPos.MapId)
                continue;

            float distToCursor = (pos.Position - cursorMapCoords.Position).LengthSquared();
            if (distToCursor < bestGhostDist)
            {
                bestGhostDist = distToCursor;
                bestGhost = uid;
            }
        }

        if (bestGhost.HasValue)
            return bestGhost.Value;

        // 2. Otherwise find the topmost constructible/deconstructible entity within interaction range
        EntityUid? bestEntity = null;
        float bestScore = float.MaxValue;

        foreach (var uid in nearby)
        {
            if (uid == player || !Exists(uid) || IsHiddenSubfloorEntity(uid))
                continue;

            var pos = _transform.GetMapCoordinates(uid);
            if (pos.MapId != playerPos.MapId)
                continue;

            if ((pos.Position - playerPos.Position).Length() > MaxInteractRange)
                continue;

            if (!_solver.TryResolveGraphAndNode(uid, out _, out _, out _))
                continue;

            float score = (pos.Position - cursorMapCoords.Position).LengthSquared();

            // Prefer above-floor structures (consoles, machines, walls, doors, frames) over subfloor pipes/cables on the same tile
            if (IsSubfloorEntity(uid))
                score += 0.02f;
            else if (IsGhostOrUnfinishedFrame(uid))
                score -= 0.01f;

            if (score < bestScore)
            {
                bestScore = score;
                bestEntity = uid;
            }
        }

        return bestEntity;
    }

    private bool IsSubfloorEntity(EntityUid uid)
    {
        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "SubFloorHideComponent", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsHiddenSubfloorEntity(EntityUid uid)
    {
        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "SubFloorHideComponent", StringComparison.OrdinalIgnoreCase))
            {
                if (GetBoolMember(comp, "IsUnderCover"))
                    return true;
            }
        }
        return false;
    }

    private bool TryRelocateTargetEntity(out EntityUid found)
        => TryRelocateReplacementEntity(EntityUid.Invalid, out found, ignorePreExisting: true);

    private bool TryRelocateReplacementEntity(EntityUid excludeEntity, out EntityUid found, bool ignorePreExisting = true)
    {
        found = EntityUid.Invalid;
        if (_targetMapCoords.MapId == MapId.Nullspace)
            return false;

        var candidates = _lookup.GetEntitiesInRange(_targetMapCoords, 0.45f, LookupFlags.Uncontained);
        float bestDist = float.MaxValue;

        foreach (var uid in candidates)
        {
            if (uid == excludeEntity || !Exists(uid) || Deleted(uid) || HasGhostComponent(uid))
                continue;

            if (ignorePreExisting && _preExistingTileEntities.Contains(uid))
                continue;

            if (!_solver.TryResolveGraphAndNode(uid, out var graph, out _, out _) || graph == null)
                continue;

            if (_activePlan != null &&
                !string.Equals(graph.ID, _activePlan.GraphId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pos = _transform.GetMapCoordinates(uid);
            float dist = (pos.Position - _targetMapCoords.Position).LengthSquared();
            if (dist < bestDist)
            {
                bestDist = dist;
                found = uid;
            }
        }

        return found.IsValid();
    }

    private bool TryRelocateNonGhostTarget(out EntityUid found)
        => TryRelocateReplacementEntity(_currentTarget, out found, ignorePreExisting: true);

    private bool IsGhostOrUnfinishedFrame(EntityUid uid)
    {
        if (HasGhostComponent(uid))
            return true;

        var meta = MetaData(uid);
        var protoId = meta.EntityPrototype?.ID ?? string.Empty;
        var name = meta.EntityName;

        foreach (var kw in UnfinishedFrameKeywords)
        {
            if (protoId.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (_solver.TryResolveGraphAndNode(uid, out _, out var node, out _) && node != null)
        {
            var nodeName = node.Name;
            foreach (var kw in UnfinishedFrameKeywords)
            {
                if (nodeName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private bool HasGhostComponent(EntityUid uid)
    {
        if (HasComp<ConstructionGhostComponent>(uid))
            return true;

        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "ConstructionGhostComponent", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsPlayerInActiveDoAfter(EntityUid player)
    {
        if (!TryComp<DoAfterComponent>(player, out var doAfterComp))
            return false;

        if (GetMemberValue(doAfterComp, "DoAfters") is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                var d = entry.Value;
                if (d == null)
                    continue;

                bool cancelled = GetBoolMember(d, "Cancelled");
                bool completed = GetBoolMember(d, "Completed");
                if (cancelled && _sawDoAfterForStep)
                {
                    _doAfterCancelledForStep = true;
                }

                if (!cancelled && !completed)
                    return true;
            }
        }

        return false;
    }

    private bool RequiresWelderIgnition(EntityUid toolUid)
    {
        foreach (var comp in EntityManager.GetComponents(toolUid))
        {
            var name = comp.GetType().Name;
            if (name.Equals("WelderComponent", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsWelderLit(EntityUid toolUid)
    {
        foreach (var comp in EntityManager.GetComponents(toolUid))
        {
            var name = comp.GetType().Name;
            if (name.Equals("ItemToggleComponent", StringComparison.OrdinalIgnoreCase))
                return GetBoolMember(comp, "Activated");
            if (name.Equals("WelderComponent", StringComparison.OrdinalIgnoreCase) &&
                GetMemberValue(comp, "Enabled") is bool enabled)
            {
                return enabled;
            }
        }
        return false;
    }

    private void ExtinguishActiveWelderIfNeeded()
    {
        if (_litWelderUid is not { } welder || !Exists(welder))
        {
            _litWelderUid = null;
            return;
        }

        if (IsWelderLit(welder) && _player.LocalEntity is { } player && Exists(player))
        {
            if (_hands.GetActiveItem(player) == welder)
                RaisePredictiveEvent(new RequestUseInHandEvent());
        }

        _litWelderUid = null;
    }

    private static object? GetMemberValue(object? obj, string name)
    {
        if (obj == null)
            return null;

        var type = obj.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;

        var prop = type.GetProperty(name, flags);
        if (prop != null)
            return prop.GetValue(obj);

        var field = type.GetField(name, flags) ?? type.GetField("_" + name, flags);
        if (field != null)
            return field.GetValue(obj);

        return null;
    }

    private IComponent? FindComponentByName(EntityUid uid, string componentTypeName)
    {
        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, componentTypeName, StringComparison.OrdinalIgnoreCase))
                return comp;
        }
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

    private static bool GetBoolMember(object? obj, string name)
        => GetMemberValue(obj, name) is true;

    private static bool TryGetStepTargetPipeLayer(ConstructionActionStep step, out int targetPipeLayer)
    {
        targetPipeLayer = 0;
        if (step.Kind != ConstructionActionKind.UseTool ||
            !step.Description.StartsWith("Set pipe layer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (step.Description.Contains("Alt1", StringComparison.OrdinalIgnoreCase) ||
            step.Description.Contains("Secondary", StringComparison.OrdinalIgnoreCase))
        {
            targetPipeLayer = 1;
            return true;
        }

        if (step.Description.Contains("Alt2", StringComparison.OrdinalIgnoreCase) ||
            step.Description.Contains("Tertiary", StringComparison.OrdinalIgnoreCase))
        {
            targetPipeLayer = 2;
            return true;
        }

        targetPipeLayer = 0;
        return true;
    }
}

/// <summary>
/// Ensures exported blueprints always capture pipe-layer positions (Primary / Alt1 / Alt2)
/// for connectors, mixers, filters, vents, pumps, and pipes on the grid.
/// </summary>
[HarmonyPatch(typeof(RobustaBlueprintManager), nameof(RobustaBlueprintManager.ExportAreaAsBlueprint))]
public static class BlueprintExportPipeLayerPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        RobustaBlueprintManager __instance,
        EntityUid gridUid,
        Vector2i corner1,
        Vector2i corner2,
        string blueprintName,
        RobustaBlueprint? __result)
    {
        if (__result == null || __result.Items.Count == 0)
            return;

        try
        {
            var entMan = IoCManager.Resolve<IEntityManager>();
            if (!entMan.EntityExists(gridUid) ||
                !entMan.TryGetComponent<MapGridComponent>(gridUid, out var grid) ||
                !entMan.EntitySysManager.TryGetEntitySystem<SharedMapSystem>(out var mapSys) ||
                !entMan.EntitySysManager.TryGetEntitySystem<RobustaConstructionSolver>(out var solver))
            {
                return;
            }

            int minX = Math.Min(corner1.X, corner2.X);
            int maxX = Math.Max(corner1.X, corner2.X);
            int minY = Math.Min(corner1.Y, corner2.Y);
            int maxY = Math.Max(corner1.Y, corner2.Y);

            var matchedItems = new HashSet<BlueprintItemEntry>();
            bool anyModified = false;

            var gridXform = entMan.GetComponent<TransformComponent>(gridUid);
            var childEnumerator = gridXform.ChildEnumerator;
            while (childEnumerator.MoveNext(out var uid))
            {
                if (!entMan.EntityExists(uid) || entMan.Deleted(uid) || entMan.IsClientSide(uid))
                    continue;

                if (entMan.HasComponent<ConstructionGhostComponent>(uid))
                    continue;

                var xform = entMan.GetComponent<TransformComponent>(uid);
                if (!xform.Anchored)
                    continue;

                var meta = entMan.GetComponent<MetaDataComponent>(uid);
                var rawProtoId = meta.EntityPrototype?.ID;
                if (string.IsNullOrEmpty(rawProtoId))
                    continue;

                var tile = mapSys.TileIndicesFor(gridUid, grid, xform.Coordinates);
                if (tile.X < minX || tile.X > maxX || tile.Y < minY || tile.Y > maxY)
                    continue;

                int ox = tile.X - minX;
                int oy = tile.Y - minY;
                var dir = xform.LocalRotation.GetCardinalDir().ToString();

                solver.ResolveExportBlueprintPrototypes(
                    uid,
                    rawProtoId,
                    out var exportedEntityProtoId,
                    out var exportedConstructionProtoId);

                // Only needed if rawProtoId differs from exportedEntityProtoId (e.g. when RobustaBlueprintManager.cs wasn't updated)
                if (string.Equals(rawProtoId, exportedEntityProtoId, StringComparison.Ordinal))
                    continue;

                foreach (var item in __result.Items)
                {
                    if (matchedItems.Contains(item))
                        continue;

                    if (item.OffsetX != ox ||
                        item.OffsetY != oy ||
                        !string.Equals(item.Direction, dir, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.Equals(item.EntityPrototypeId, rawProtoId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    matchedItems.Add(item);
                    item.EntityPrototypeId = exportedEntityProtoId;
                    if (!string.IsNullOrEmpty(exportedConstructionProtoId))
                        item.ConstructionPrototypeId = exportedConstructionProtoId;
                    anyModified = true;
                    break;
                }
            }

            if (anyModified)
            {
                __instance.SaveBlueprint(__result, blueprintName);
            }
        }
        catch
        {
            // Ignore patch errors so blueprint export never throws
        }
    }
}