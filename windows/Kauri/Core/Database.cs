using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kauri.Core;

public static class ClipTypes
{
    public const string String = "String";
    public const string Rtf = "RTF";
    public const string Rtfd = "RTFD";
    public const string Html = "HTML";
    public const string Pdf = "PDF";
    public const string Filenames = "Filenames";
    public const string Url = "URL";
    public const string Image = "TIFF";
}

public sealed record History(string Id, string Title, IReadOnlyList<string> Types, long CreatedAt, long UpdateAt)
{
    public string TypedTitle
    {
        get
        {
            var primary = Types.Count > 0 ? Types[0] : null;
            string? prefix = primary switch
            {
                ClipTypes.Image => "(Image)",
                ClipTypes.Pdf => "(PDF)",
                ClipTypes.Filenames => "(Files)",
                ClipTypes.Html => "(HTML)",
                _ => null,
            };
            var t = TextUtil.TrimmedMenuTitle(Title);
            return string.Join(" ", new[] { prefix, t }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }
}

public sealed record ClipAsset(string Type, byte[] Data);
public sealed record Thumbnail(string Kind, byte[] Data); // kind: image | colorCode
public sealed record HistoryDetail(History History, Thumbnail? Thumbnail);

public sealed class SnippetFolder
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "untitled folder";
    public int Index { get; set; }
    public bool IsEnabled { get; set; } = true;
    public List<Snippet> Snippets { get; } = new();
}

public sealed class Snippet
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid FolderId { get; set; }
    public string Title { get; set; } = "untitled snippet";
    public string Content { get; set; } = "";
    public int Index { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public static class TextUtil
{
    static readonly char[] LineBreaks = { '\r', '\n', '\u2028', '\u2029', '\u0085', '\v', '\f' };

    public static string TrimmedMenuTitle(string s)
    {
        var title = s.Trim();
        var nl = title.IndexOfAny(LineBreaks);
        if (nl >= 0) title = title[..nl];
        const string shorten = "...";
        var max = Math.Max(AppSettings.Shared.MaximumMenuItemTitleLength, shorten.Length);
        var info = new System.Globalization.StringInfo(title);
        if (info.LengthInTextElements <= max) return title;
        return info.SubstringByTextElements(0, max - shorten.Length) + shorten;
    }

    public static string Prefix(string s, int n)
    {
        var info = new System.Globalization.StringInfo(s);
        return info.LengthInTextElements <= n ? s : info.SubstringByTextElements(0, n);
    }
}

public sealed class Database
{
    readonly string _connectionString;
    public static Database Shared { get; private set; } = null!;
    public event Action? HistoriesChanged;
    public event Action? SnippetsChanged;

    Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = true }.ToString();
    }

    public static void Open()
    {
        Directory.CreateDirectory(AppSettings.Directory);
        Shared = new Database(Path.Combine(AppSettings.Directory, "kauri.db"));
        Shared.Migrate();
    }

    SqliteConnection Connect()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    void Migrate()
    {
        using var c = Connect();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS histories(
              id TEXT PRIMARY KEY NOT NULL, title TEXT NOT NULL DEFAULT '', types TEXT NOT NULL DEFAULT '[]',
              createdAt INTEGER NOT NULL DEFAULT 0, updateAt INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS histories_updateAt ON histories(updateAt);
            CREATE TABLE IF NOT EXISTS historyAssets(
              historyId TEXT NOT NULL REFERENCES histories(id) ON DELETE CASCADE,
              idx INTEGER NOT NULL, type TEXT NOT NULL, data BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS historyAssets_history ON historyAssets(historyId, idx);
            CREATE TABLE IF NOT EXISTS historyThumbnails(
              historyId TEXT PRIMARY KEY NOT NULL REFERENCES histories(id) ON DELETE CASCADE,
              kind TEXT NOT NULL, data BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS snippetFolders(
              id TEXT PRIMARY KEY NOT NULL, title TEXT NOT NULL DEFAULT '', idx INTEGER NOT NULL DEFAULT 0,
              isEnabled INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS snippets(
              id TEXT PRIMARY KEY NOT NULL, folderId TEXT NOT NULL REFERENCES snippetFolders(id) ON DELETE CASCADE,
              title TEXT NOT NULL DEFAULT '', content TEXT NOT NULL DEFAULT '', idx INTEGER NOT NULL DEFAULT 0,
              isEnabled INTEGER NOT NULL DEFAULT 1);
            """);
    }

    static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ---------------- Histories ----------------

    public List<HistoryDetail> FetchHistoryDetails(bool sortsByCreatedAt, bool includesThumbnail, int limit)
    {
        using var c = Connect();
        using var cmd = c.CreateCommand();
        var order = sortsByCreatedAt ? "h.createdAt DESC, h.updateAt DESC" : "h.updateAt DESC";
        cmd.CommandText = includesThumbnail
            ? $"SELECT h.id,h.title,h.types,h.createdAt,h.updateAt,t.kind,t.data FROM histories h LEFT JOIN historyThumbnails t ON t.historyId=h.id ORDER BY {order} LIMIT $l"
            : $"SELECT h.id,h.title,h.types,h.createdAt,h.updateAt,NULL,NULL FROM histories h ORDER BY {order} LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<HistoryDetail>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var h = new History(r.GetString(0), r.GetString(1),
                JsonSerializer.Deserialize<List<string>>(r.GetString(2)) ?? new(), r.GetInt64(3), r.GetInt64(4));
            Thumbnail? t = r.IsDBNull(5) ? null : new Thumbnail(r.GetString(5), (byte[])r[6]);
            list.Add(new HistoryDetail(h, t));
        }
        return list;
    }

    public bool HistoryExists(string id)
    {
        using var c = Connect();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM histories WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() != null;
    }

    public List<ClipAsset> FetchAssets(string id)
    {
        using var c = Connect();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT type,data FROM historyAssets WHERE historyId=$id ORDER BY idx";
        cmd.Parameters.AddWithValue("$id", id);
        var list = new List<ClipAsset>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new ClipAsset(r.GetString(0), (byte[])r[1]));
        return list;
    }

    public void SaveHistory(string id, string title, IReadOnlyList<string> types, IReadOnlyList<ClipAsset> assets, Thumbnail? thumbnail, long updateAt)
    {
        using (var c = Connect())
        using (var tx = c.BeginTransaction())
        {
            long createdAt = updateAt;
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT createdAt FROM histories WHERE id=$id";
                q.Parameters.AddWithValue("$id", id);
                if (q.ExecuteScalar() is long existing) createdAt = existing;
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO histories(id,title,types,createdAt,updateAt) VALUES($id,$t,$ty,$c,$u)
                    ON CONFLICT(id) DO UPDATE SET title=excluded.title, types=excluded.types, updateAt=excluded.updateAt;
                    DELETE FROM historyAssets WHERE historyId=$id;
                    DELETE FROM historyThumbnails WHERE historyId=$id;
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$t", TextUtil.Prefix(title, 10000));
                cmd.Parameters.AddWithValue("$ty", JsonSerializer.Serialize(types));
                cmd.Parameters.AddWithValue("$c", createdAt);
                cmd.Parameters.AddWithValue("$u", updateAt);
                cmd.ExecuteNonQuery();
            }
            for (int i = 0; i < assets.Count; i++)
            {
                using var a = c.CreateCommand();
                a.Transaction = tx;
                a.CommandText = "INSERT INTO historyAssets(historyId,idx,type,data) VALUES($id,$i,$t,$d)";
                a.Parameters.AddWithValue("$id", id);
                a.Parameters.AddWithValue("$i", i);
                a.Parameters.AddWithValue("$t", assets[i].Type);
                a.Parameters.AddWithValue("$d", assets[i].Data);
                a.ExecuteNonQuery();
            }
            if (thumbnail != null)
            {
                using var t = c.CreateCommand();
                t.Transaction = tx;
                t.CommandText = "INSERT INTO historyThumbnails(historyId,kind,data) VALUES($id,$k,$d)";
                t.Parameters.AddWithValue("$id", id);
                t.Parameters.AddWithValue("$k", thumbnail.Kind);
                t.Parameters.AddWithValue("$d", thumbnail.Data);
                t.ExecuteNonQuery();
            }
            tx.Commit();
        }
        HistoriesChanged?.Invoke();
    }

    public void DeleteHistory(string id)
    {
        using (var c = Connect()) Exec(c, "DELETE FROM histories WHERE id=$id", ("$id", id));
        HistoriesChanged?.Invoke();
    }

    public void DeleteAllHistories()
    {
        using (var c = Connect())
        {
            Exec(c, "DELETE FROM historyAssets; DELETE FROM historyThumbnails; DELETE FROM histories;");
            Exec(c, "VACUUM;");
        }
        HistoriesChanged?.Invoke();
    }

    public void DeleteOverflowingHistories(bool sortsByCreatedAt, int max)
    {
        var order = sortsByCreatedAt ? "createdAt DESC, updateAt DESC" : "updateAt DESC";
        int n;
        using (var c = Connect())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"DELETE FROM histories WHERE id NOT IN (SELECT id FROM histories ORDER BY {order} LIMIT $m)";
            cmd.Parameters.AddWithValue("$m", max);
            n = cmd.ExecuteNonQuery();
        }
        if (n > 0) HistoriesChanged?.Invoke();
    }

    // ---------------- Snippets ----------------

    public List<SnippetFolder> FetchFolders()
    {
        using var c = Connect();
        var folders = new List<SnippetFolder>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id,title,idx,isEnabled FROM snippetFolders ORDER BY idx";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                folders.Add(new SnippetFolder { Id = Guid.Parse(r.GetString(0)), Title = r.GetString(1), Index = r.GetInt32(2), IsEnabled = r.GetInt64(3) != 0 });
        }
        var byId = folders.ToDictionary(f => f.Id);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id,folderId,title,content,idx,isEnabled FROM snippets ORDER BY idx";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var s = new Snippet
                {
                    Id = Guid.Parse(r.GetString(0)), FolderId = Guid.Parse(r.GetString(1)), Title = r.GetString(2),
                    Content = r.GetString(3), Index = r.GetInt32(4), IsEnabled = r.GetInt64(5) != 0,
                };
                if (byId.TryGetValue(s.FolderId, out var f)) f.Snippets.Add(s);
            }
        }
        return folders;
    }

    public SnippetFolder InsertFolder(string title = "untitled folder")
    {
        var f = new SnippetFolder { Title = title };
        using (var c = Connect())
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COALESCE(MAX(idx)+1,0) FROM snippetFolders";
            f.Index = Convert.ToInt32(q.ExecuteScalar());
            Exec(c, "INSERT INTO snippetFolders(id,title,idx,isEnabled) VALUES($id,$t,$i,1)", ("$id", f.Id.ToString()), ("$t", f.Title), ("$i", f.Index));
        }
        SnippetsChanged?.Invoke();
        return f;
    }

    public Snippet InsertSnippet(Guid folderId, string title = "untitled snippet", string content = "")
    {
        var s = new Snippet { FolderId = folderId, Title = title, Content = content };
        using (var c = Connect())
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COALESCE(MAX(idx)+1,0) FROM snippets WHERE folderId=$f";
            q.Parameters.AddWithValue("$f", folderId.ToString());
            s.Index = Convert.ToInt32(q.ExecuteScalar());
            Exec(c, "INSERT INTO snippets(id,folderId,title,content,idx,isEnabled) VALUES($id,$f,$t,$c,$i,1)",
                ("$id", s.Id.ToString()), ("$f", folderId.ToString()), ("$t", title), ("$c", content), ("$i", s.Index));
        }
        SnippetsChanged?.Invoke();
        return s;
    }

    public void UpdateFolder(SnippetFolder f)
    {
        using (var c = Connect())
            Exec(c, "UPDATE snippetFolders SET title=$t, isEnabled=$e WHERE id=$id", ("$t", f.Title), ("$e", f.IsEnabled ? 1 : 0), ("$id", f.Id.ToString()));
        SnippetsChanged?.Invoke();
    }

    public void UpdateSnippet(Snippet s, bool notify = true)
    {
        using (var c = Connect())
            Exec(c, "UPDATE snippets SET title=$t, content=$c, isEnabled=$e, folderId=$f WHERE id=$id",
                ("$t", s.Title), ("$c", s.Content), ("$e", s.IsEnabled ? 1 : 0), ("$f", s.FolderId.ToString()), ("$id", s.Id.ToString()));
        if (notify) SnippetsChanged?.Invoke();
    }

    public void UpdateFolderIndexes(IReadOnlyList<SnippetFolder> folders)
    {
        using (var c = Connect())
        using (var tx = c.BeginTransaction())
        {
            for (int i = 0; i < folders.Count; i++)
            {
                folders[i].Index = i;
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE snippetFolders SET idx=$i WHERE id=$id";
                cmd.Parameters.AddWithValue("$i", i);
                cmd.Parameters.AddWithValue("$id", folders[i].Id.ToString());
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        SnippetsChanged?.Invoke();
    }

    public void UpdateSnippetIndexes(Guid folderId, IReadOnlyList<Snippet> snippets)
    {
        using (var c = Connect())
        using (var tx = c.BeginTransaction())
        {
            for (int i = 0; i < snippets.Count; i++)
            {
                snippets[i].Index = i;
                snippets[i].FolderId = folderId;
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE snippets SET idx=$i, folderId=$f WHERE id=$id";
                cmd.Parameters.AddWithValue("$i", i);
                cmd.Parameters.AddWithValue("$f", folderId.ToString());
                cmd.Parameters.AddWithValue("$id", snippets[i].Id.ToString());
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        SnippetsChanged?.Invoke();
    }

    public void DeleteFolder(Guid id)
    {
        using (var c = Connect()) Exec(c, "DELETE FROM snippetFolders WHERE id=$id", ("$id", id.ToString()));
        SnippetsChanged?.Invoke();
    }

    public void DeleteSnippet(Guid id)
    {
        using (var c = Connect()) Exec(c, "DELETE FROM snippets WHERE id=$id", ("$id", id.ToString()));
        SnippetsChanged?.Invoke();
    }
}
