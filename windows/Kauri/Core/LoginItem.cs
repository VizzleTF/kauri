using Microsoft.Win32;

namespace Kauri.Core;

/// <summary>Launch-at-login via the per-user Run key.</summary>
public static class LoginItem
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = AppInfo.Name;

    public static void Sync() => Apply(AppSettings.Shared.LaunchAtLogin);

    public static void Apply(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            // Remove the entry written by pre-rename builds of this port.
            if (k.GetValue("Clipy") is string old && old.Contains("Clipy.exe") && !old.Contains("/Applications")) k.DeleteValue("Clipy");
            if (enabled)
                k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else if (k.GetValue(Name) != null)
                k.DeleteValue(Name);
        }
        catch { }
    }
}
