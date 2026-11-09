using System.Globalization;
using System.Windows.Media;

namespace Kauri.Core;

/// <summary>Hex colour parsing compatible with SwiftHEXColors (used by Clipy for colour previews).</summary>
public static class ColorCode
{
    public static bool TryParse(string text, out Color color)
    {
        color = default;
        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length is not (3 or 4 or 6 or 8)) return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        byte r, g, b, a = 255;
        switch (s.Length)
        {
            case 3:
                r = (byte)(((v >> 8) & 0xF) * 17); g = (byte)(((v >> 4) & 0xF) * 17); b = (byte)((v & 0xF) * 17);
                break;
            case 4:
                r = (byte)(((v >> 12) & 0xF) * 17); g = (byte)(((v >> 8) & 0xF) * 17); b = (byte)(((v >> 4) & 0xF) * 17); a = (byte)((v & 0xF) * 17);
                break;
            case 6:
                r = (byte)(v >> 16); g = (byte)(v >> 8); b = (byte)v;
                break;
            default:
                r = (byte)(v >> 24); g = (byte)(v >> 16); b = (byte)(v >> 8); a = (byte)v;
                break;
        }
        color = Color.FromArgb(a, r, g, b);
        return true;
    }
}
