using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskDock.Remote;

public sealed record ActivityEntry(
    DateTimeOffset Ts, string DeviceId, string DeviceName, string Type, string Target, bool Ok, string? Code = null, string? Preview = null);

/// <summary>
/// remote\activity-YYYY-MM.jsonl, one line per remote action. Files untouched for 30 days are deleted at startup.
/// Never holds tokens or nonces; an approval's preview is cut to 200 characters.
/// </summary>
public sealed class ActivityLog
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    readonly string _dir;
    readonly TimeProvider _time;
    readonly Lock _lock = new();

    public ActivityLog(string dir, TimeProvider time)
    {
        (_dir, _time) = (dir, time);
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(dir, "activity-*.jsonl"))
            if (time.GetUtcNow() - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(30)) File.Delete(f);
    }

    public void Add(ActivityEntry e)
    {
        if (e.Preview?.Length > 200) e = e with { Preview = e.Preview[..200] };
        lock (_lock) File.AppendAllText(Path.Combine(_dir, $"activity-{e.Ts:yyyy-MM}.jsonl"), JsonSerializer.Serialize(e, Json) + "\n");
    }

    /// <summary>Newest first, up to 50 entries older than <paramref name="before"/>.</summary>
    public List<ActivityEntry> Page(DateTimeOffset? before, int size = 50)
    {
        // shortcut: reads the whole files (30 days of taps is small); keep an index if anyone ever logs thousands a day.
        string[] lines;
        lock (_lock) lines = Directory.EnumerateFiles(_dir, "activity-*.jsonl").OrderDescending().Take(2).SelectMany(File.ReadAllLines).ToArray();
        return lines.Select(Parse).OfType<ActivityEntry>()
            .Where(e => before == null || e.Ts < before).OrderByDescending(e => e.Ts).Take(size).ToList();
    }

    static ActivityEntry? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<ActivityEntry>(line, Json); }
        catch (JsonException) { return null; } // a line cut short by a crash
    }
}
