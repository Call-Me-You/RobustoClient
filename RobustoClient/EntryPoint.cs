using Robust.Client.Input;
using Robust.Shared.ContentPack;

// Based on the original Arabica client by noverd. Massive thanks for the foundational framework.
namespace RobustoClient;

public sealed class EntryPoint : GameShared
{
    [Dependency] private readonly IInputManager _inputManager = default!;

    // Overwites upstream similar keybinds
    private const int CommandBindingPriority = 10;

    public override void PostInit()
    {
        RobustaConfig.Load();
        MarseyLogger.Info($"Config: {RobustaConfig.ConfigPath}");
        MarseyLogger.Debug("Building graph...");
        IoCManager.BuildGraph();
        MarseyLogger.Debug("Graph success built!");
        IoCManager.InjectDependencies(this);

        RegisterCommand("robusta.toggle_menu", RobustaConfig.ToggleMenuKey);
        RegisterCommand("robusta.auto_construct", RobustaConfig.AutoConstructKey);
        RegisterCommand("robusta.auto_deconstruct", RobustaConfig.AutoDeconstructKey);
        RegisterCommand("robusta.hack", RobustaConfig.HackingKey);

        MarseyLogger.Debug("Setup context.");
    }

    private void RegisterCommand(string function, RobustaKeybind bind)
    {
        _inputManager.RegisterBinding(new KeyBindingRegistration
        {
            Function = function,
            BaseKey = bind.BaseKey,
            Mod1 = bind.Mod1,
            Mod2 = bind.Mod2,
            Mod3 = bind.Mod3,
            Priority = CommandBindingPriority,
            Type = KeyBindingType.Command
        }, false);

        MarseyLogger.Info($"Bound {function} to {bind}.");
    }
}