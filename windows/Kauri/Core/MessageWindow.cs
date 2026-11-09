using System.Windows.Interop;

namespace Kauri.Core;

/// <summary>Hidden message-only window that receives WM_HOTKEY and WM_CLIPBOARDUPDATE.</summary>
public sealed class MessageWindow : IDisposable
{
    readonly HwndSource _source;
    public IntPtr Handle => _source.Handle;
    public event Action<int>? HotKey;
    public event Action? ClipboardUpdate;

    public static MessageWindow Shared { get; } = new();

    MessageWindow()
    {
        var p = new HwndSourceParameters("KauriMessageWindow") { Width = 0, Height = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            HotKey?.Invoke(wParam.ToInt32());
            handled = true;
        }
        else if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            ClipboardUpdate?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => _source.Dispose();
}
