namespace Kauri.Core;

/// <summary>Product identity in one place.</summary>
public static class AppInfo
{
    /// <summary>Short name: executable, data folder, startup entry, menu items.</summary>
    public const string Name = "Kauri";

    /// <summary>Display name: window titles, tray tooltip, About.</summary>
    public const string DisplayName = "Kauri Clip";

    /// <summary>Project page; About hides the link when it is null.</summary>
    public const string? HomepageUrl = "https://github.com/VizzleTF/kauri";

    public const string UpstreamName = "Clipy";
    public const string UpstreamUrl = "https://github.com/Clipy/Clipy";

    public static string Version
    {
        get
        {
            var v = typeof(AppInfo).Assembly.GetName().Version;
            return v == null ? "—" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
