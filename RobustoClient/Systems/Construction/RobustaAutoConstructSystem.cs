using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Client.Construction;
using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;
using Content.Shared.DoAfter;
using Content.Shared.Hands;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Content.Shared.Tools.Systems;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using RobustoClient.Systems.Abstract;
using RobustoClient.Systems.Construction.Blueprints;

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
    [Dependency] private readonly SharedToolSystem _toolSystem = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly ItemUtilitySystem _itemUtil = default!;
    [Dependency] private readonly RobustaConstructionSolver _solver = default!;
    [Dependency] private readonly ConstructionSystem _clientConstruction = default!;

    private ISawmill _log = default!;

    private const float MaxInteractRange = 2.5f;
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
    private int _stepRetryCount;
    private int _welderIgniteRetries;
    private bool _sawDoAfterForStep;
    private bool _doAfterCancelledForStep;
    private float _stepWaitElapsed;
    private float _postDoAfterTimer;

    private readonly List<(EntityUid GridUid, Vector2i Tile, Direction Dir, BlueprintItemEntry Item)> _blueprintQueue = new();
    public IReadOnlyList<(EntityUid GridUid, Vector2i Tile, Direction Dir, BlueprintItemEntry Item)> PendingBlueprintTasks => _blueprintQueue;
    public BlueprintZPhase? CurrentActiveBlueprintPhase => _blueprintQueue.Count > 0 ? _blueprintQueue.Min(t => t.Item.Phase) : null;
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

        // Start short window to distinguish 1 short tap (Build) vs double-tap / long-hold (Deconstruct)
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
            plan = _solver.SolveBuildPlan(target.Value);
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
        _waitTimer = 0f;
        _rescanTimer = 0f;
        _wasInDoAfter = false;
        _stepRetryCount = 0;
        _welderIgniteRetries = 0;
        _sawDoAfterForStep = false;
        _doAfterCancelledForStep = false;
        _stepWaitElapsed = 0f;
        _postDoAfterTimer = 0f;

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

        _activePlan = null;
        _stepIndex = 0;
        _phase = StepExecutionPhase.PrepareAndEquip;
        _currentTarget = EntityUid.Invalid;
        _waitTimer = 0f;
        _rescanTimer = 0f;
        _wasInDoAfter = false;
        _stepStartTarget = EntityUid.Invalid;
        _stepStartNode = null;
        _stepRetryCount = 0;
        _welderIgniteRetries = 0;
        _sawDoAfterForStep = false;
        _doAfterCancelledForStep = false;
        _stepWaitElapsed = 0f;
        _postDoAfterTimer = 0f;
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
            .Where(i => onlyPhase == null || i.Phase == onlyPhase.Value)
            .OrderBy(i => i.Phase)
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
            int c = a.Item.Phase.CompareTo(b.Item.Phase);
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
                // otherwise -> Short-tap Build!
                bool isHolding = _input.IsKeyDown(RobustaConfig.AutoConstructKey);
                TriggerAtCursor(forceDeconstruct: isHolding);
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

        if (_itemUtil.IsBusy)
            return;

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
        while (_blueprintQueue.Count > 0)
        {
            var minPhase = _blueprintQueue.Min(t => t.Item.Phase);
            int idx = _blueprintQueue.FindIndex(t => t.Item.Phase == minPhase);
            if (idx < 0)
                return;

            var task = _blueprintQueue[idx];
            if (!Exists(task.GridUid) || !TryComp<MapGridComponent>(task.GridUid, out var grid))
            {
                _blueprintQueue.RemoveAt(idx);
                continue;
            }

            var tileCoords = _mapSystem.GridTileToLocal(task.GridUid, grid, task.Tile);
            var tileMapCoords = _transform.ToMapCoordinates(tileCoords);
            var playerMapCoords = _transform.GetMapCoordinates(player);

            if (tileMapCoords.MapId != playerMapCoords.MapId)
            {
                _blueprintQueue.RemoveAt(idx);
                continue;
            }

            // Skip if target structure is already present on this tile
            if (IsBlueprintItemAlreadySatisfied(tileMapCoords, task.Item))
            {
                _blueprintQueue.RemoveAt(idx);
                continue;
            }

            // Check if there is a nearer task in the SAME phase within interaction range
            float distToPlayer = (tileMapCoords.Position - playerMapCoords.Position).Length();
            if (distToPlayer > MaxInteractRange)
            {
                int inRangeIdx = -1;
                float bestDist = float.MaxValue;
                for (int i = 0; i < _blueprintQueue.Count; i++)
                {
                    var candidate = _blueprintQueue[i];
                    if (candidate.Item.Phase != minPhase || !Exists(candidate.GridUid) ||
                        !TryComp<MapGridComponent>(candidate.GridUid, out var cGrid))
                        continue;

                    var cCoords = _mapSystem.GridTileToLocal(candidate.GridUid, cGrid, candidate.Tile);
                    var cMap = _transform.ToMapCoordinates(cCoords);
                    if (cMap.MapId != playerMapCoords.MapId)
                        continue;

                    float d = (cMap.Position - playerMapCoords.Position).Length();
                    if (d <= MaxInteractRange && d < bestDist)
                    {
                        bestDist = d;
                        inRangeIdx = i;
                    }
                }

                if (inRangeIdx >= 0)
                {
                    idx = inRangeIdx;
                    task = _blueprintQueue[idx];
                    grid = Comp<MapGridComponent>(task.GridUid);
                    tileCoords = _mapSystem.GridTileToLocal(task.GridUid, grid, task.Tile);
                    tileMapCoords = _transform.ToMapCoordinates(tileCoords);
                }
            }

            _blueprintQueue.RemoveAt(idx);

            // 1. Direct stack placement (Cables, Floor Tiles, Rods/Lattice)
            if (TryExecuteDirectStackBlueprintItem(task.Item, tileCoords))
            {
                _waitTimer = SettleDelay;
                return;
            }

            // 2. Existing unfinished frame or ghost on the tile
            var existingFrameOrGhost = FindMatchingFrameOrGhostOnTile(tileMapCoords, task.Item);
            if (existingFrameOrGhost != null && Exists(existingFrameOrGhost.Value))
            {
                var existingPlan = _solver.SolveBuildPlan(existingFrameOrGhost.Value, task.Item.EntityPrototypeId);
                if (existingPlan != null && existingPlan.Steps.Count > 0)
                {
                    StartJob(existingFrameOrGhost.Value, existingPlan);
                    return;
                }
            }

            // 3. Spawn construction ghost and start auto-building it
            var recipeId = task.Item.ConstructionPrototypeId;
            if (string.IsNullOrEmpty(recipeId) || !_protoMan.HasIndex<ConstructionPrototype>(recipeId))
                recipeId = _solver.ResolveConstructionPrototypeForEntity(task.Item.EntityPrototypeId);

            if (!string.IsNullOrEmpty(recipeId) &&
                _protoMan.TryIndex<ConstructionPrototype>(recipeId, out var cProto) &&
                cProto.Type == ConstructionType.Structure)
            {
                if (_clientConstruction.TrySpawnGhost(cProto, tileCoords, task.Dir, out var ghostUid) &&
                    ghostUid != null && Exists(ghostUid.Value))
                {
                    var ghostPlan = _solver.SolveBuildPlan(cProto, includeGhostStart: true)
                                    ?? _solver.SolveBuildPlan(ghostUid.Value);
                    if (ghostPlan != null && ghostPlan.Steps.Count > 0)
                    {
                        StartJob(ghostUid.Value, ghostPlan);
                        return;
                    }
                }
            }
        }
    }

    private bool IsBlueprintItemAlreadySatisfied(MapCoordinates tileMapCoords, BlueprintItemEntry item)
    {
        if (string.IsNullOrEmpty(item.EntityPrototypeId))
            return false;

        var entities = _lookup.GetEntitiesInRange(tileMapCoords, 0.35f, LookupFlags.Uncontained);
        foreach (var uid in entities)
        {
            if (!Exists(uid) || Deleted(uid) || HasGhostComponent(uid))
                continue;

            var protoId = MetaData(uid).EntityPrototype?.ID;
            if (string.Equals(protoId, item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private EntityUid? FindMatchingFrameOrGhostOnTile(MapCoordinates tileMapCoords, BlueprintItemEntry item)
    {
        var entities = _lookup.GetEntitiesInRange(tileMapCoords, 0.35f, LookupFlags.Uncontained);
        foreach (var uid in entities)
        {
            if (!Exists(uid) || Deleted(uid))
                continue;

            if (HasGhostComponent(uid))
                return uid;

            if (IsGhostOrUnfinishedFrame(uid) && _solver.TryResolveGraphAndNode(uid, out _, out _, out _))
                return uid;
        }

        return null;
    }

    private bool TryExecuteDirectStackBlueprintItem(BlueprintItemEntry item, EntityCoordinates tileCoords)
    {
        var idLower = (item.EntityPrototypeId ?? string.Empty).ToLowerInvariant();
        string? stackProtoOrType = null;

        if (idLower.Contains("cablehv"))
            stackProtoOrType = "CableHVStack";
        else if (idLower.Contains("cablemv"))
            stackProtoOrType = "CableMVStack";
        else if (idLower.Contains("cableapc"))
            stackProtoOrType = "CableApcExtensionStack";
        else if (idLower.Contains("floortile") || idLower == "tileitemsteel")
            stackProtoOrType = "FloorTileItemSteel";

        if (string.IsNullOrEmpty(stackProtoOrType))
            return false;

        bool equipped = _itemUtil.TryEquipItem(stackProtoOrType, 1) ||
                        _itemUtil.TryEquipByPredicate(
                            uid => TryComp<StackComponent>(uid, out var st) &&
                                   st.StackTypeId.Id.Contains(
                                       idLower.Contains("cablehv") ? "CableHV" :
                                       idLower.Contains("cablemv") ? "CableMV" :
                                       idLower.Contains("cableapc") ? "Cable" : "FloorTile",
                                       StringComparison.OrdinalIgnoreCase),
                            $"DirectStack:{stackProtoOrType}",
                            1);

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

        // Track target across entity replacements (e.g. Ghost -> Girder -> WallSolid, or WallSolid -> Girder)
        if (!Exists(_currentTarget) || Deleted(_currentTarget))
        {
            if (TryRelocateTargetEntity(out var replacement))
            {
                _currentTarget = replacement;
                _targetCoordinates = Transform(replacement).Coordinates;
                _targetMapCoords = _transform.GetMapCoordinates(replacement);
                _rescanTimer = 0f;
                _stepRetryCount = 0;
                AdvancePastAlreadyReachedNodes();
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

        switch (_phase)
        {
            case StepExecutionPhase.PrepareAndEquip:
            {
                if (!PrepareAndEquipForStep(player, step))
                {
                    _log.Warning($"Failed to equip required item for step [{_stepIndex + 1}/{_activePlan.Steps.Count}]: {step.Description}");
                    FinishCurrentPlan();
                    return;
                }

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
                        _stepIndex++;
                        AdvancePastAlreadyReachedNodes();
                        _phase = StepExecutionPhase.PrepareAndEquip;
                        return;
                    }

                    _rescanTimer += frameTime;
                    if (_rescanTimer >= TargetRescanTimeout)
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
                        bool nodeAlreadyChanged = false;
                        if (Exists(_currentTarget) &&
                            _solver.TryResolveGraphAndNode(_currentTarget, out _, out var checkNode, out _) &&
                            checkNode != null &&
                            !string.IsNullOrEmpty(step.ExpectedNextNode) &&
                            !string.Equals(step.ExpectedNextNode, _stepStartNode, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(checkNode.Name, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase))
                        {
                            nodeAlreadyChanged = true;
                        }

                        if (!nodeAlreadyChanged && _stepWaitElapsed < Math.Min(0.85f, step.DoAfterDelay + 0.4f))
                        {
                            return;
                        }

                        if (!nodeAlreadyChanged && step.DoAfterDelay >= 0.45f)
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

                // If this step is the final step of an edge that replaces/deletes the entity (e.g. target node, start, or girder), wait for replacement
                bool expectsEntityReplacement =
                    !string.IsNullOrEmpty(step.ExpectedNextNode) &&
                    !string.Equals(step.ExpectedNextNode, _stepStartNode, StringComparison.OrdinalIgnoreCase) &&
                    (_stepIndex + 1 >= _activePlan.Steps.Count ||
                     !string.Equals(_activePlan.Steps[_stepIndex + 1].ExpectedNextNode, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase)) &&
                    (string.Equals(step.ExpectedNextNode, _activePlan.TargetNode, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(step.ExpectedNextNode, "start", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(step.ExpectedNextNode, "girder", StringComparison.OrdinalIgnoreCase));

                if (expectsEntityReplacement && Exists(_currentTarget) && _currentTarget == _stepStartTarget)
                {
                    var curNodeName = _solver.TryResolveGraphAndNode(_currentTarget, out _, out var cNode, out _) && cNode != null
                        ? cNode.Name
                        : null;

                    if (!string.Equals(curNodeName, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase))
                    {
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
                }

                _stepIndex++;
                _rescanTimer = 0f;
                _stepRetryCount = 0;
                _phase = StepExecutionPhase.PrepareAndEquip;
                break;
            }
        }
    }

    private void AdvancePastAlreadyReachedNodes()
    {
        if (_activePlan == null || !Exists(_currentTarget))
            return;

        if (!_solver.TryResolveGraphAndNode(_currentTarget, out _, out var actualNode, out _) || actualNode == null)
            return;

        var actualNodeName = actualNode.Name;
        if (string.IsNullOrEmpty(actualNodeName))
            return;

        // Advance past all contiguous steps that were part of the transition INTO actualNodeName,
        // stopping before any synthetic Anchoring step (ExpectedNextNode == null) or the next edge.
        for (int i = _stepIndex; i < _activePlan.Steps.Count; i++)
        {
            if (string.Equals(_activePlan.Steps[i].ExpectedNextNode, actualNodeName, StringComparison.OrdinalIgnoreCase))
            {
                int nextIdx = i + 1;
                while (nextIdx < _activePlan.Steps.Count &&
                       string.Equals(_activePlan.Steps[nextIdx].ExpectedNextNode, actualNodeName, StringComparison.OrdinalIgnoreCase))
                {
                    nextIdx++;
                }
                _stepIndex = nextIdx;
                return;
            }
        }
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

                // Try equipping by StackTypeId or prototype ID
                if (_itemUtil.TryEquipItem(new ProtoId<StackPrototype>(matId), step.Amount))
                    return true;

                return _itemUtil.TryEquipByPredicate(
                    uid => TryComp<StackComponent>(uid, out var stack) &&
                           string.Equals(stack.StackTypeId, matId, StringComparison.OrdinalIgnoreCase),
                    $"Material:{matId}",
                    step.Amount);
            }

            case ConstructionActionKind.UseTool:
            {
                var quality = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(quality))
                    return false;

                return _itemUtil.TryEquipByPredicate(
                    uid => _toolSystem.HasQuality(uid, quality),
                    $"Tool:{quality}");
            }

            case ConstructionActionKind.InsertPrototype:
            {
                var protoId = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(protoId))
                    return false;

                return _itemUtil.TryEquipItem(new EntProtoId(protoId), step.Amount);
            }

            case ConstructionActionKind.InsertComponent:
            {
                var compName = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(compName))
                    return false;

                return _itemUtil.TryEquipByPredicate(
                    uid => EntityManager.GetComponents(uid).Any(c =>
                        c.GetType().Name.Equals(compName, StringComparison.OrdinalIgnoreCase) ||
                        c.GetType().Name.Equals(compName + "Component", StringComparison.OrdinalIgnoreCase)),
                    $"Component:{compName}",
                    step.Amount);
            }

            case ConstructionActionKind.InsertTag:
            {
                var tag = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(tag))
                    return false;

                return _itemUtil.TryEquipByPredicate(
                    uid => _tagSystem.HasTag(uid, tag),
                    $"Tag:{tag}",
                    step.Amount);
            }

            default:
                return false;
        }
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
            if (uid == player || !Exists(uid))
                continue;

            var pos = _transform.GetMapCoordinates(uid);
            if (pos.MapId != playerPos.MapId)
                continue;

            if ((pos.Position - playerPos.Position).Length() > MaxInteractRange)
                continue;

            if (!_solver.TryResolveGraphAndNode(uid, out _, out _, out _))
                continue;

            float distSq = (pos.Position - cursorMapCoords.Position).LengthSquared();
            // Prefer unfinished frames slightly if right on the same tile
            if (IsGhostOrUnfinishedFrame(uid))
                distSq -= 0.01f;

            if (distSq < bestScore)
            {
                bestScore = distSq;
                bestEntity = uid;
            }
        }

        return bestEntity;
    }

    private bool TryRelocateTargetEntity(out EntityUid found)
    {
        found = EntityUid.Invalid;
        if (_targetMapCoords.MapId == MapId.Nullspace)
            return false;

        var candidates = _lookup.GetEntitiesInRange(_targetMapCoords, 0.45f, LookupFlags.Uncontained);
        float bestDist = float.MaxValue;

        foreach (var uid in candidates)
        {
            if (!Exists(uid) || Deleted(uid) || HasGhostComponent(uid))
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
        => TryRelocateTargetEntity(out found);

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
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        var prop = type.GetProperty(name, flags);
        if (prop != null)
            return prop.GetValue(obj);

        var field = type.GetField(name, flags);
        if (field != null)
            return field.GetValue(obj);

        return null;
    }

    private static bool GetBoolMember(object? obj, string name)
        => GetMemberValue(obj, name) is true;
}
