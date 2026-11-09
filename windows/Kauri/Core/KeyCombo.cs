using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Kauri.Core;

/// <summary>
/// Modifier flags use the macOS vocabulary so the UI can render the same glyphs as Clipy for Mac.
/// Mapping to Windows: ⌘ Command = Ctrl, ⌥ Option = Alt, ⌃ Control = Win, ⇧ Shift = Shift.
/// </summary>
[Flags]
public enum MacModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Option = 4,
    Command = 8,
}

public sealed record KeyCombo(int VirtualKey, MacModifiers Modifiers)
{
    [JsonIgnore]
    public uint Win32Modifiers
    {
        get
        {
            uint m = 0x4000; // MOD_NOREPEAT
            if (Modifiers.HasFlag(MacModifiers.Option)) m |= 0x1;   // MOD_ALT
            if (Modifiers.HasFlag(MacModifiers.Command)) m |= 0x2;  // MOD_CONTROL
            if (Modifiers.HasFlag(MacModifiers.Shift)) m |= 0x4;    // MOD_SHIFT
            if (Modifiers.HasFlag(MacModifiers.Control)) m |= 0x8;  // MOD_WIN
            return m;
        }
    }

    [JsonIgnore]
    public string KeyName => KeyNames.Name(VirtualKey);

    /// <summary>Windows names in the conventional order Win, Ctrl, Alt, Shift.</summary>
    public static IEnumerable<string> ModifierNames(MacModifiers m)
    {
        if (m.HasFlag(MacModifiers.Control)) yield return "Win";
        if (m.HasFlag(MacModifiers.Command)) yield return "Ctrl";
        if (m.HasFlag(MacModifiers.Option)) yield return "Alt";
        if (m.HasFlag(MacModifiers.Shift)) yield return "Shift";
    }

    public override string ToString() => string.Join("+", ModifierNames(Modifiers).Append(KeyName));

    public static MacModifiers FromWpf(ModifierKeys keys, bool winDown)
    {
        var m = MacModifiers.None;
        if (keys.HasFlag(ModifierKeys.Shift)) m |= MacModifiers.Shift;
        if (keys.HasFlag(ModifierKeys.Control)) m |= MacModifiers.Command;
        if (keys.HasFlag(ModifierKeys.Alt)) m |= MacModifiers.Option;
        if (keys.HasFlag(ModifierKeys.Windows) || winDown) m |= MacModifiers.Control;
        return m;
    }

    public static readonly KeyCombo DefaultMain = new(0x56, MacModifiers.Shift | MacModifiers.Command);
    public static readonly KeyCombo DefaultHistory = new(0x56, MacModifiers.Control | MacModifiers.Command);
    public static readonly KeyCombo DefaultSnippet = new(0x42, MacModifiers.Shift | MacModifiers.Command);
    public static readonly KeyCombo DefaultSettings = new(0x56, MacModifiers.Shift | MacModifiers.Control | MacModifiers.Option | MacModifiers.Command);
}

public static class KeyNames
{
    public static string Name(int vk)
    {
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return ((char)('0' + vk - 0x60)).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PgUp",
            0x22 => "PgDn",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x2E => "Del",
            0x6A => "*",
            0x6B => "+",
            0x6D => "-",
            0x6E => ".",
            0x6F => "/",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"#{vk}",
        };
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;
}
