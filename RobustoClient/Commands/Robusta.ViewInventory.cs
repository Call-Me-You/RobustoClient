using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using RobustoClient.Systems;

namespace RobustoClient.Commands;

[AnyCommand]
public sealed class ViewInventoryCommand : IConsoleCommand
{
    public string Command => "robusta.view_inv";
    public string Description => "Opens the inventory viewer for the entity under cursor, locked target, or self.";
    public string Help => "robusta.view_inv [optional EntityUid]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var invSys = entMan.System<RobustaInventoryViewerSystem>();

        if (args.Length == 1 && int.TryParse(args[0], out var rawUid))
        {
            var target = new EntityUid(rawUid);
            if (entMan.EntityExists(target))
            {
                invSys.OpenInventoryWindow(target);
                shell.WriteLine($"[InventoryViewer] Opened inventory for entity {target}.");
                return;
            }

            shell.WriteError($"Entity {target} does not exist.");
            return;
        }

        invSys.OpenForHoverOrTarget();
        shell.WriteLine("[InventoryViewer] Opened inventory viewer.");
    }
}