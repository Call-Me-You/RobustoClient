using System;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using RobustoClient.Systems.Construction;

namespace RobustoClient.Commands;

[AnyCommand]
public sealed class AutoConstructCommand : IConsoleCommand
{
    public string Command => "robusta.auto_construct";
    public string Description => "Automatically builds (short tap V) or deconstructs (double-tap V / held AutoDeconstructKey) the structure/ghost under the cursor.";
    public string Help => "Usage: robusta.auto_construct [build|deconstruct|stop]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var sys = entMan.System<RobustaAutoConstructSystem>();

        if (args.Length == 0)
        {
            sys.HandleConstructKeyTap();
            return;
        }

        var mode = args[0].ToLowerInvariant();
        switch (mode)
        {
            case "build":
                sys.TriggerAtCursor(forceDeconstruct: false);
                break;
            case "deconstruct":
            case "decon":
                sys.TriggerAtCursor(forceDeconstruct: true);
                break;
            case "stop":
            case "cancel":
                sys.StopJob();
                shell.WriteLine("[AutoConstruct] Stopped active job.");
                break;
            default:
                shell.WriteError($"Unknown subcommand '{args[0]}'. {Help}");
                break;
        }
    }
}

[AnyCommand]
public sealed class AutoDeconstructCommand : IConsoleCommand
{
    public string Command => "robusta.auto_deconstruct";
    public string Description => "Automatically deconstructs the structure under the cursor.";
    public string Help => "Usage: robusta.auto_deconstruct";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        entMan.System<RobustaAutoConstructSystem>().HandleDeconstructKeyTap();
    }
}
