using System.Collections.Specialized;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Kauri.Core;

public sealed class ClipContent
{
    public required List<ClipAsset> Assets { get; init; }
    public List<string> Types => Assets.Select(a => a.Type).ToList();

    public string? StringValue
    {
        get
        {
            var a = Assets.FirstOrDefault(x => x.Type == ClipTypes.String);
            if (a != null) return Encoding.UTF8.GetString(a.Data);
            var f = Assets.FirstOrDefault(x => x.Type == ClipTypes.Filenames);
            if (f != null) return string.Join("\n", Encoding.UTF8.GetString(f.Data).Split('\n').Select(Path.GetFileName));
            return null;
        }
    }

    public bool IsBlankText
    {
        get
        {
            var primary = Assets.Count > 0 ? Assets[0].Type : null;
            if (primary is not (ClipTypes.String or ClipTypes.Rtf)) return false;
            var s = StringValue;
            return s != null && string.IsNullOrWhiteSpace(s);
        }
    }

    public string Hash
    {
        get
        {
            using var ms = new MemoryStream();
            void Append(byte[] d)
            {
                var len = BitConverter.GetBytes((ulong)d.Length);
                if (BitConverter.IsLittleEndian) Array.Reverse(len);
                ms.Write(len);
                ms.Write(d);
            }
            foreach (var a in Assets)
            {
                Append(Encoding.UTF8.GetBytes(a.Type));
                Append(a.Data);
            }
            return Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
        }
    }

    public byte[]? ImageData
    {
        get
        {
            var img = Assets.FirstOrDefault(a => a.Type == ClipTypes.Image);
            if (img != null) return img.Data;
            var f = Assets.FirstOrDefault(a => a.Type == ClipTypes.Filenames);
            if (f == null) return null;
            var path = Encoding.UTF8.GetString(f.Data).Split('\n')
                .FirstOrDefault(p => new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".tif", ".gif" }.Contains(Path.GetExtension(p).ToLowerInvariant()));
            try { return path != null && File.Exists(path) && new FileInfo(path).Length < 64 * 1024 * 1024 ? File.ReadAllBytes(path) : null; }
            catch { return null; }
        }
    }

    public void WriteToClipboard(bool plainTextOnly)
    {
        var data = new DataObject();
        if (plainTextOnly)
        {
            data.SetText(StringValue ?? "", TextDataFormat.UnicodeText);
        }
        else
        {
            foreach (var a in Assets)
            {
                switch (a.Type)
                {
                    case ClipTypes.String: data.SetText(Encoding.UTF8.GetString(a.Data), TextDataFormat.UnicodeText); break;
                    case ClipTypes.Rtf: data.SetData(DataFormats.Rtf, Encoding.UTF8.GetString(a.Data)); break;
                    case ClipTypes.Html: data.SetData(DataFormats.Html, Encoding.UTF8.GetString(a.Data)); break;
                    case ClipTypes.Url:
                        data.SetData("UniformResourceLocatorW", new MemoryStream(Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(a.Data) + "\0")));
                        break;
                    case ClipTypes.Filenames:
                        var sc = new StringCollection();
                        sc.AddRange(Encoding.UTF8.GetString(a.Data).Split('\n', StringSplitOptions.RemoveEmptyEntries));
                        data.SetFileDropList(sc);
                        break;
                    case ClipTypes.Pdf: data.SetData("Portable Document Format", new MemoryStream(a.Data)); break;
                    case ClipTypes.Image:
                        try
                        {
                            var bmp = BitmapFrame.Create(new MemoryStream(a.Data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            data.SetImage(bmp);
                            data.SetData("PNG", new MemoryStream(a.Data));
                        }
                        catch { }
                        break;
                }
            }
        }
        for (int i = 0; i < 10; i++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch { Thread.Sleep(30); }
        }
    }
}

public sealed class ClipService
{
    public static ClipService Shared { get; } = new();
    readonly DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    DispatcherTimer? _cleanupTimer;
    int _ignoreNext;
    static readonly uint CfExclude = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    static readonly uint CfCanIncludeHistory = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    static readonly uint CfViewerIgnore = Native.RegisterClipboardFormat("Clipboard Viewer Ignore");

    public void StartMonitoring()
    {
        MessageWindow.Shared.ClipboardUpdate += () =>
            // Clipboard owners often still hold the clipboard right after the notification.
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, OnClipboardChanged);
        Native.AddClipboardFormatListener(MessageWindow.Shared.Handle);
        _trimTimer.Tick += (_, _) => Database.Shared.DeleteOverflowingHistories(!AppSettings.Shared.ReordersClipsAfterPasting, AppSettings.Shared.MaximumHistoryCount);
        _trimTimer.Start();
        AppSettings.Changed += n =>
        {
            if (n is nameof(AppSettings.ClearsHistoryPeriodically) or nameof(AppSettings.HistoryClearInterval)) ScheduleCleanup();
        };
        ScheduleCleanup();
    }

    void ScheduleCleanup()
    {
        _cleanupTimer?.Stop();
        _cleanupTimer = null;
        if (!AppSettings.Shared.ClearsHistoryPeriodically) return;
        _cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds((int)AppSettings.Shared.HistoryClearInterval) };
        _cleanupTimer.Tick += (_, _) => Database.Shared.DeleteAllHistories();
        _cleanupTimer.Start();
    }

    public void IgnoreNextChange() => _ignoreNext++;

    public void ApplicationWillTerminate()
    {
        if (AppSettings.Shared.ClearsHistoryOnQuit) Database.Shared.DeleteAllHistories();
    }

    void OnClipboardChanged()
    {
        if (_ignoreNext > 0) { _ignoreNext--; return; }
        try { Create(); } catch { /* clipboard can be locked by other apps */ }
    }

    void Create()
    {
        var s = AppSettings.Shared;
        if (Native.IsClipboardFormatAvailable(CfViewerIgnore)) return;
        if (s.IgnoresConcealedPasteboardTypes && (Native.IsClipboardFormatAvailable(CfExclude) || Native.IsClipboardFormatAvailable(CfCanIncludeHistory) && IsCanIncludeFalse()))
            return;
        if (IsExcludedApplication()) return;

        IDataObject? data = null;
        for (int i = 0; i < 5 && data == null; i++)
        {
            try { data = Clipboard.GetDataObject(); }
            catch { Thread.Sleep(40); }
        }
        if (data == null) return;
        var content = Read(data, s);
        if (content == null) return;
        Save(content);
    }

    static bool IsCanIncludeFalse()
    {
        try
        {
            if (Clipboard.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
                return BitConverter.ToInt32(ms.ToArray(), 0) == 0;
        }
        catch { }
        return false;
    }

    static bool IsExcludedApplication()
    {
        var list = AppSettings.Shared.ExcludedApplications;
        if (list.Count == 0) return false;
        var path = Native.ProcessPathForWindow(Native.GetForegroundWindow());
        var owner = Native.ProcessPathForWindow(Native.GetClipboardOwner());
        return list.Any(a => string.Equals(a.Identifier, path, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(a.Identifier, owner, StringComparison.OrdinalIgnoreCase));
    }

    static ClipContent? Read(IDataObject data, AppSettings s)
    {
        var assets = new List<ClipAsset>();
        bool hasFiles = s.IsTypeEnabled(ClipTypes.Filenames) && data.GetDataPresent(DataFormats.FileDrop);
        bool hasText = s.IsTypeEnabled(ClipTypes.String) && data.GetDataPresent(DataFormats.UnicodeText);
        bool hasImage = s.IsTypeEnabled(ClipTypes.Image) && (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG"));

        if (hasFiles && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            assets.Add(new ClipAsset(ClipTypes.Filenames, Encoding.UTF8.GetBytes(string.Join("\n", files))));
        if (!hasFiles && !hasText && hasImage)
        {
            var png = ReadImagePng(data);
            if (png != null) assets.Add(new ClipAsset(ClipTypes.Image, png));
        }
        if (hasText && data.GetData(DataFormats.UnicodeText) is string text)
            assets.Add(new ClipAsset(ClipTypes.String, Encoding.UTF8.GetBytes(text)));
        if (s.IsTypeEnabled(ClipTypes.Rtf) && data.GetDataPresent(DataFormats.Rtf) && data.GetData(DataFormats.Rtf) is string rtf)
            assets.Add(new ClipAsset(ClipTypes.Rtf, Encoding.UTF8.GetBytes(rtf)));
        if (s.IsTypeEnabled(ClipTypes.Html) && data.GetDataPresent(DataFormats.Html) && data.GetData(DataFormats.Html) is string html)
            assets.Add(new ClipAsset(ClipTypes.Html, Encoding.UTF8.GetBytes(html)));
        if (s.IsTypeEnabled(ClipTypes.Url) && data.GetDataPresent("UniformResourceLocatorW") && data.GetData("UniformResourceLocatorW") is MemoryStream url)
            assets.Add(new ClipAsset(ClipTypes.Url, Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(url.ToArray()).TrimEnd('\0'))));
        if (s.IsTypeEnabled(ClipTypes.Pdf) && data.GetDataPresent("Portable Document Format") && data.GetData("Portable Document Format") is MemoryStream pdf)
            assets.Add(new ClipAsset(ClipTypes.Pdf, pdf.ToArray()));
        if ((hasFiles || hasText) && hasImage && assets.All(a => a.Type != ClipTypes.Image))
        {
            var png = ReadImagePng(data);
            if (png != null) assets.Add(new ClipAsset(ClipTypes.Image, png));
        }
        return assets.Count == 0 ? null : new ClipContent { Assets = assets };
    }

    static byte[]? ReadImagePng(IDataObject data)
    {
        try
        {
            BitmapSource? src = null;
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream pngStream)
                src = BitmapFrame.Create(pngStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            src ??= Clipboard.GetImage();
            if (src == null) return null;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    public void Save(ClipContent content)
    {
        var s = AppSettings.Shared;
        var hash = content.Hash;
        if (!s.AllowsDuplicateHistory && Database.Shared.HistoryExists(hash)) return;
        if (content.IsBlankText) return;
        var id = s.OverwritesDuplicateHistory ? hash : Guid.NewGuid().ToString().ToUpperInvariant();
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Database.Shared.SaveHistory(id, content.StringValue ?? "", content.Types, content.Assets, MakeThumbnail(content), unix);
        if (Database.Shared.HistoryExists(id))
            Database.Shared.DeleteOverflowingHistories(!s.ReordersClipsAfterPasting, s.MaximumHistoryCount);
    }

    static Thumbnail? MakeThumbnail(ClipContent content)
    {
        var img = content.ImageData;
        if (img != null)
        {
            try
            {
                var frame = BitmapFrame.Create(new MemoryStream(img), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                // Store at 2x of the largest allowed thumbnail so it stays crisp on HiDPI screens.
                double scale = Math.Min(1.0, Math.Min(1024.0 / frame.PixelWidth, 1024.0 / frame.PixelHeight));
                BitmapSource src = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(src));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return new Thumbnail("image", ms.ToArray());
            }
            catch { return null; }
        }
        var str = content.Assets.FirstOrDefault(a => a.Type == ClipTypes.String);
        if (str != null && content.Assets[0].Type == ClipTypes.String && ColorCode.TryParse(Encoding.UTF8.GetString(str.Data), out var color))
            return new Thumbnail("colorCode", Encoding.UTF8.GetBytes(color.ToString()));
        return null;
    }
}
