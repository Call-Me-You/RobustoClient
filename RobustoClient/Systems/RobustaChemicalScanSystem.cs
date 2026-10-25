using System;
using System.Linq;
using System.Reflection;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Examine;
using Content.Shared.Verbs;
using Robust.Shared.Utility;
using Robust.Client.Player;
using Content.Client.Examine;

namespace RobustoClient.Systems;

// We no longer use LocalPlayerAddCompSystem for the Shared-component
// to avoid synchronization conflicts (flickering).
public sealed class RobustaChemicalScanSystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPlayerManager _player = default!;

    private Type? _solutionManagerType;
    private Type? _entityWrapperType;
    private MethodInfo? _tryGetSolutionMethod;
    private MethodInfo? _getSolutionExamineMethod;
    private bool _reflectionInitialized;

    public override void Initialize()
    {
        base.Initialize();
        
        // 1. Always allow solution scanning on client
        SubscribeLocalEvent<SolutionScanEvent>(OnSolutionScan);
        
        // 2. Add analysis button (client verb)
        SubscribeLocalEvent<GetVerbsEvent<ExamineVerb>>(OnGetExamineVerbs);

        // 3. INTERCEPT SERVER MESSAGES
        // To prevent server from overwriting our examine window with "empty" data
        SubscribeNetworkEvent<ExamineSystemMessages.ExamineInfoResponseMessage>(OnExamineInfoResponse);
    }

    private void OnSolutionScan(SolutionScanEvent args)
    {
        args.CanScan = true; // Always allow scanning
    }

    private static object? GetMemberValue(object? obj, string name)
    {
        if (obj == null) return null;
        var type = obj.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var prop = type.GetProperty(name, flags);
        if (prop != null) return prop.GetValue(obj);
        var field = type.GetField(name, flags);
        if (field != null) return field.GetValue(obj);
        return null;
    }

    private void EnsureReflection(SharedSolutionContainerSystem solutionSystem)
    {
        if (_reflectionInitialized) return;
        _reflectionInitialized = true;

        _solutionManagerType = typeof(SharedSolutionContainerSystem).Assembly
            .GetType("Content.Shared.Chemistry.Components.SolutionManager.SolutionManagerComponent")
            ?? typeof(SharedSolutionContainerSystem).Assembly
            .GetType("Content.Shared.Chemistry.Components.SolutionContainerManagerComponent");

        if (_solutionManagerType == null) return;

        _entityWrapperType = typeof(Entity<>).MakeGenericType(_solutionManagerType);
        var sysType = solutionSystem.GetType();

        _tryGetSolutionMethod = sysType.GetMethods()
            .FirstOrDefault(m => m.Name == "TryGetSolution" &&
                                 m.GetParameters().Length == 5 &&
                                 m.GetParameters()[0].ParameterType.IsGenericType &&
                                 m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Entity<>));

        _getSolutionExamineMethod = sysType.GetMethod("GetSolutionExamine",
            BindingFlags.Instance | BindingFlags.NonPublic);
    }

    private bool TryBuildSolutionExamineMessage(EntityUid uid, out FormattedMessage? chemMsg)
    {
        chemMsg = null;

        object? component = null;
        foreach (var comp in _entMan.GetComponents(uid))
        {
            if (comp.GetType().Name == "ExaminableSolutionComponent")
            {
                component = comp;
                break;
            }
        }
        if (component == null) return false;

        var solutionName = GetMemberValue(component, "Solution") as string ?? "default";
        var solutionSystem = _entMan.System<SharedSolutionContainerSystem>();
        EnsureReflection(solutionSystem);

        if (_entityWrapperType == null || _tryGetSolutionMethod == null || _getSolutionExamineMethod == null)
            return false;

        var entityInstance = Activator.CreateInstance(_entityWrapperType, uid, null);
        var parameters = new object?[] { entityInstance, solutionName, null, null, false };
        var result = (bool)_tryGetSolutionMethod.Invoke(solutionSystem, parameters)!;

        if (!result || parameters[3] is not Solution solution)
            return false;

        chemMsg = (FormattedMessage)_getSolutionExamineMethod.Invoke(solutionSystem, [solution])!;
        return true;
    }

    private void OnExamineInfoResponse(ExamineSystemMessages.ExamineInfoResponseMessage ev)
    {
        var uid = _entMan.GetEntity(ev.EntityUid);
        if (TryBuildSolutionExamineMessage(uid, out var chemMsg) && chemMsg != null)
        {
            ev.Message.PushNewline();
            ev.Message.AddMessage(chemMsg);
        }
    }

    private void OnGetExamineVerbs(GetVerbsEvent<ExamineVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null || args.User != localPlayer)
            return;

        if (!TryBuildSolutionExamineMessage(args.Target, out _))
            return;

        var verb = new ExamineVerb()
        {
            Act = () =>
            {
                if (TryBuildSolutionExamineMessage(args.Target, out var chemMsg) && chemMsg != null)
                {
                    var finalMsg = new FormattedMessage();
                    finalMsg.AddText("[Robusta Analysis]");
                    finalMsg.PushNewline();
                    finalMsg.AddMessage(chemMsg);

                    _entMan.System<ExamineSystem>().SendExamineTooltip(args.User, args.Target, finalMsg, false, false);
                }
            },
            Text = "Chem-analysis (Robusta)",
            Category = VerbCategory.Examine,
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/drink.svg.192dpi.png")),
            ClientExclusive = true,
            Priority = 10
        };

        args.Verbs.Add(verb);
    }
}