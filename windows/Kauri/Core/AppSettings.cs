using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kauri.Core;

public enum HistoryClearInterval
{
    FiveMinutes = 300,
    ThirtyMinutes = 1800,
    OneHour = 3600,
    FourHours = 14400,
    EightHours = 28800,
    OneDay = 86400,
    ThreeDays = 259200,
    SevenDays = 604800,
}

public sealed record ExcludedApplication(string Identifier, string Name);

/// <summary>Mirror of Clipy's AppStorage values with identical defaults.</summary>
public sealed class AppSettings
{
    // General
    public bool LaunchAtLogin { get; set; }
    public bool SuppressLoginItemAlert { get; set; }
    public bool PastesAutomatically { get; set; } = true;
    public int MaximumHistoryCount { get; set; } = 30;
    public bool ReordersClipsAfterPasting { get; set; } = true;
    public bool ClearsHistoryOnQuit { get; set; }
    public bool ClearsHistoryPeriodically { get; set; }
    public HistoryClearInterval HistoryClearInterval { get; set; } = HistoryClearInterval.OneHour;
    public int StatusItemDisplayMode { get; set; } = 1; // 0 hidden, 1 standard, 2 reversed
    public bool CollectsCrashReports { get; set; } = true;

    // Menu
    public int InlineMenuItemLimit { get; set; }
    public int FolderMenuItemLimit { get; set; } = 10;
    public int MaximumMenuItemTitleLength { get; set; } = 20;
    public bool AllowsDuplicateHistory { get; set; } = true;
    public bool OverwritesDuplicateHistory { get; set; } = true;
    public bool ShowsIconsInMenu { get; set; } = true;
    public bool MarksMenuItemsWithNumbers { get; set; } = true;
    public bool StartsMenuItemTitlesAtZero { get; set; }
    public bool AddsNumericKeyEquivalents { get; set; }
    public bool ShowsSnippetsAboveHistory { get; set; }
    public bool ShowsClearHistoryMenuItem { get; set; } = true;
    public bool ShowsClearHistoryAlert { get; set; } = true;
    public bool ShowsToolTipsOnMenuItems { get; set; } = true;
    public int MaximumToolTipLength { get; set; } = 200;
    public bool ShowsImagesInMenu { get; set; } = true;
    public int ThumbnailWidth { get; set; } = 100;
    public int ThumbnailHeight { get; set; } = 32;
    public bool ShowsColorPreviewInMenu { get; set; } = true;

    // Type
    public Dictionary<string, bool> PasteboardTypes { get; set; } = new();
    public bool IgnoresConcealedPasteboardTypes { get; set; }
    public bool IgnoresUniversalClipboard { get; set; }

    // Exclude
    public List<ExcludedApplication> ExcludedApplications { get; set; } = new();

    // Shortcuts
    public KeyCombo? MainKeyCombo { get; set; } = KeyCombo.DefaultMain;
    public KeyCombo? HistoryKeyCombo { get; set; } = KeyCombo.DefaultHistory;
    public KeyCombo? SnippetKeyCombo { get; set; } = KeyCombo.DefaultSnippet;
    public KeyCombo? EditSnippetsKeyCombo { get; set; }
    public KeyCombo? ClearHistoryKeyCombo { get; set; }
    public KeyCombo SettingsKeyCombo { get; set; } = KeyCombo.DefaultSettings;
    public Dictionary<string, KeyCombo> FolderKeyCombos { get; set; } = new();

    // Updates
    public bool AutomaticallyChecksForUpdates { get; set; }
    public double UpdateCheckInterval { get; set; } = 86400;
    public DateTime? LastUpdateCheckDate { get; set; }

    // Beta
    public bool PastesPlainTextWithModifier { get; set; } = true;
    public int PlainTextPasteModifier { get; set; }
    public bool DeletesHistoryWithModifier { get; set; }
    public int HistoryDeletionModifier { get; set; }
    public bool PastesAndDeletesHistoryWithModifier { get; set; }
    public int PasteAndDeleteHistoryModifier { get; set; }
    public bool ObservesScreenshots { get; set; }

    public bool IsTypeEnabled(string type) => !PasteboardTypes.TryGetValue(type, out var v) || v;

    // ---- persistence ----
    [JsonIgnore] public static string Directory { get; } = Environment.GetEnvironmentVariable("KAURI_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);
    [JsonIgnore] static string FilePath => Path.Combine(Directory, "settings.json");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Shared { get; private set; } = new();

    /// <summary>Raised (on the UI thread) after any property is changed through <see cref="Update"/>.</summary>
    public static event Action<string>? Changed;

    public static void Load()
    {
        MigrateLegacyFolder();
        try
        {
            if (File.Exists(FilePath))
                Shared = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            Shared = new AppSettings();
        }
    }

    /// <summary>Early builds of this port stored data in %APPDATA%\Clipy; move it over once.</summary>
    static void MigrateLegacyFolder()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("KAURI_DATA_DIR") != null || System.IO.Directory.Exists(Directory)) return;
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clipy");
            if (!File.Exists(Path.Combine(legacy, "settings.json"))) return;
            System.IO.Directory.CreateDirectory(Directory);
            File.Copy(Path.Combine(legacy, "settings.json"), FilePath);
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                var src = Path.Combine(legacy, "clipy.db" + ext);
                if (File.Exists(src)) File.Copy(src, Path.Combine(Directory, "kauri.db" + ext));
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Shared, JsonOptions));
            File.Move(tmp, FilePath, true);
        }
        catch
        {
            // Settings are best effort; never crash the app on a disk error.
        }
    }

    public static void Update(string name, Action<AppSettings> change)
    {
        change(Shared);
        Save();
        Changed?.Invoke(name);
    }
}
