using System.IO;
using System.Text.Json;
using NetHog.Models;

namespace NetHog.Services;

/// <summary>
/// Stores completed control sessions as user data. It never writes cache or temp files.
/// </summary>
public sealed class SessionHistoryStore
{
    private readonly object _sync = new();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetHog",
        "session-history.json");
    private readonly string _legacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WifiBox",
        "session-history.json");

    public IReadOnlyList<SessionHistoryRecord> Load(TimeSpan? retention = null)
    {
        lock (_sync)
        {
            var records = LoadInternal();
            if (retention is not { } window || window <= TimeSpan.Zero) return records;

            var retained = RetainRecent(records, window);
            if (retained.Count == records.Count) return records;

            try
            {
                SaveInternal(retained);
                return retained;
            }
            catch
            {
                return records;
            }
        }
    }

    public int Prune(TimeSpan? retention)
    {
        if (retention is not { } window || window <= TimeSpan.Zero) return 0;

        lock (_sync)
        {
            var records = LoadInternal();
            var retained = RetainRecent(records, window);
            if (retained.Count == records.Count) return 0;

            SaveInternal(retained);
            return records.Count - retained.Count;
        }
    }

    public void Add(SessionHistoryRecord record, TimeSpan? retention = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            var records = LoadInternal();
            records.Insert(0, record);
            if (retention is { } window && window > TimeSpan.Zero)
            {
                records = RetainRecent(records, window);
            }
            SaveInternal(records);
        }
    }

    public bool Delete(Guid id)
    {
        lock (_sync)
        {
            var records = LoadInternal();
            var removed = records.RemoveAll(record => record.Id == id);
            if (removed == 0) return false;
            SaveInternal(records);
            return true;
        }
    }

    public int DeleteAll()
    {
        lock (_sync)
        {
            var records = LoadInternal();
            var removed = records.Count;
            if (File.Exists(_path)) File.Delete(_path);
            if (File.Exists(_legacyPath)) File.Delete(_legacyPath);
            return removed;
        }
    }

    private List<SessionHistoryRecord> LoadInternal()
    {
        try
        {
            var sourcePath = File.Exists(_path) ? _path : _legacyPath;
            if (!File.Exists(sourcePath)) return new List<SessionHistoryRecord>();
            return JsonSerializer.Deserialize<List<SessionHistoryRecord>>(File.ReadAllText(sourcePath))
                   ?? new List<SessionHistoryRecord>();
        }
        catch
        {
            return new List<SessionHistoryRecord>();
        }
    }

    private void SaveInternal(IReadOnlyList<SessionHistoryRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(records, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static List<SessionHistoryRecord> RetainRecent(
        IReadOnlyList<SessionHistoryRecord> records,
        TimeSpan retention)
    {
        var cutoff = DateTimeOffset.Now - retention;
        return records.Where(record => record.EndedAt >= cutoff).ToList();
    }
}
