using System.IO;
using System.Text.Json;
using WifiBox.Models;

namespace WifiBox.Services;

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

    public IReadOnlyList<SessionHistoryRecord> Load()
    {
        lock (_sync)
        {
            try
            {
                var sourcePath = File.Exists(_path) ? _path : _legacyPath;
                if (!File.Exists(sourcePath)) return Array.Empty<SessionHistoryRecord>();
                return JsonSerializer.Deserialize<List<SessionHistoryRecord>>(File.ReadAllText(sourcePath))
                       ?? new List<SessionHistoryRecord>();
            }
            catch
            {
                return Array.Empty<SessionHistoryRecord>();
            }
        }
    }

    public void Add(SessionHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            var records = LoadInternal();
            records.Insert(0, record);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(records, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
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
}
