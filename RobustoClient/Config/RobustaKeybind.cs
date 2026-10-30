using System.Text.Json.Serialization;
using Robust.Client.Input;

namespace RobustoClient;

/// <summary>
/// A key combination: one base key plus up to three held modifiers, mirroring upstream
/// <see cref="KeyBindingRegistration"/>'s <c>BaseKey</c>/<c>Mod1</c>/<c>Mod2</c>/<c>Mod3</c> model.
/// <see cref="Keyboard.Key.Unknown"/> means "no modifier".
/// </summary>
[JsonConverter(typeof(RobustaKeybindJsonConverter))]
public readonly record struct RobustaKeybind
{
    public Keyboard.Key BaseKey { get; }

    public Keyboard.Key Mod1 { get; }

    public Keyboard.Key Mod2 { get; }

    public Keyboard.Key Mod3 { get; }

    public RobustaKeybind(
        Keyboard.Key baseKey,
        Keyboard.Key mod1 = Keyboard.Key.Unknown,
        Keyboard.Key mod2 = Keyboard.Key.Unknown,
        Keyboard.Key mod3 = Keyboard.Key.Unknown)
    {
        // Modifiers are sorted like upstream so the order a user writes them in never changes identity.
        if (mod1 < mod2) (mod1, mod2) = (mod2, mod1);
        if (mod2 < mod3) (mod2, mod3) = (mod3, mod2);
        if (mod1 < mod2) (mod1, mod2) = (mod2, mod1);

        BaseKey = baseKey;
        Mod1 = mod1;
        Mod2 = mod2;
        Mod3 = mod3;
    }

    /// <summary>Whether the combination has a real base key and no repeated keys.</summary>
    public bool IsValid =>
        BaseKey != Keyboard.Key.Unknown
        && Enum.IsDefined(BaseKey)
        && !HasDuplicateKeys();

    /// <summary>True while every key of the combination is held.</summary>
    public bool IsDown(IInputManager input)
    {
        if (BaseKey == Keyboard.Key.Unknown || !input.IsKeyDown(BaseKey))
            return false;

        return (Mod1 == Keyboard.Key.Unknown || input.IsKeyDown(Mod1))
               && (Mod2 == Keyboard.Key.Unknown || input.IsKeyDown(Mod2))
               && (Mod3 == Keyboard.Key.Unknown || input.IsKeyDown(Mod3));
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Mod3 != Keyboard.Key.Unknown) parts.Add(Mod3.ToString());
        if (Mod2 != Keyboard.Key.Unknown) parts.Add(Mod2.ToString());
        if (Mod1 != Keyboard.Key.Unknown) parts.Add(Mod1.ToString());
        parts.Add(BaseKey.ToString());

        return string.Join("+", parts);
    }

    private bool HasDuplicateKeys()
    {
        return Duplicate(BaseKey, Mod1) || Duplicate(BaseKey, Mod2) || Duplicate(BaseKey, Mod3)
            || Duplicate(Mod1, Mod2) || Duplicate(Mod1, Mod3) || Duplicate(Mod2, Mod3);

        static bool Duplicate(Keyboard.Key a, Keyboard.Key b)
            => a != Keyboard.Key.Unknown && a == b;
    }
}