using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Kauri.Core;

public sealed class PasteService
{
    public static PasteService Shared { get; } = new();

    /// <summary>Window that was in front before Clipy showed a menu; paste goes there.</summary>
    public IntPtr TargetWindow { get; set; }

    // Modifier picker values in Clipy: 0 Command(⌘=Ctrl), 1 Shift, 2 Control(⌃=Win), 3 Option(⌥=Alt)
    static bool IsPressedModifier(int flag) => flag switch
    {
        0 => Native.IsKeyDown(0x11),
        1 => Native.IsKeyDown(0x10),
        2 => Native.IsKeyDown(0x5B) || Native.IsKeyDown(0x5C),
        3 => Native.IsKeyDown(0x12),
        _ => false,
    };

    public void PasteHistory(string id)
    {
        var s = AppSettings.Shared;
        var assets = Database.Shared.FetchAssets(id);
        if (assets.Count == 0) return;
        var content = new ClipContent { Assets = assets };
        bool plain = s.PastesPlainTextWithModifier && IsPressedModifier(s.PlainTextPasteModifier);
        bool delete = s.DeletesHistoryWithModifier && IsPressedModifier(s.HistoryDeletionModifier);
        bool pasteAndDelete = s.PastesAndDeletesHistoryWithModifier && IsPressedModifier(s.PasteAndDeleteHistoryModifier);

        if (!plain && !delete && !pasteAndDelete)
        {
            content.WriteToClipboard(false);
            Paste();
            return;
        }
        if (pasteAndDelete) ClipService.Shared.IgnoreNextChange();
        if (plain)
        {
            content.WriteToClipboard(true);
            Paste();
        }
        else if (pasteAndDelete)
        {
            content.WriteToClipboard(false);
            Paste();
        }
        if (delete || pasteAndDelete) Database.Shared.DeleteHistory(id);
    }

    public void PasteText(string text)
    {
        new ClipContent { Assets = new() { new ClipAsset(ClipTypes.String, System.Text.Encoding.UTF8.GetBytes(text)) } }.WriteToClipboard(true);
        Paste();
    }

    public void Paste()
    {
        if (!AppSettings.Shared.PastesAutomatically) return;
        var target = TargetWindow;
        Native.ForceForeground(target);
        // Give the target a moment to become active, then synthesize Ctrl+V.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var inputs = new List<Native.INPUT>();
            // Release modifiers that are still physically held (e.g. from the hot key).
            foreach (var vk in new ushort[] { 0x10, 0x12, 0x5B, 0x5C })
                if (Native.IsKeyDown(vk)) inputs.Add(Native.Key(vk, true));
            bool ctrlHeld = Native.IsKeyDown(0x11);
            if (!ctrlHeld) inputs.Add(Native.Key(0x11, false));
            inputs.Add(Native.Key(0x56, false));
            inputs.Add(Native.Key(0x56, true));
            if (!ctrlHeld) inputs.Add(Native.Key(0x11, true));
            Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>());
        };
        timer.Start();
    }
}
