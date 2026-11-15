using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskDock.Core.Protocol;

// Wire format, version 1. protocol/taskdock.schema.json is the source of truth.
public sealed record Envelope(int V, string Type, long Seq, long Ts, Source? Src, JsonElement Body);

public sealed record Source(string Adapter, string Instance);

public sealed record HelloBody(
    string Adapter, string Instance, string? Version = null, int? Pid = null,
    Dictionary<string, string>? Match = null, string[]? Actions = null, string? Token = null);

public sealed record WelcomeBody(int HeartbeatMs, bool NotifyEnabled);

public sealed record StateBody(string State, string Label, string? Detail = null, double? Progress = null, JsonElement? Items = null);

/// <summary>Unity sends PulseAgeMs: how long ago EditorApplication.update last ran. Other adapters send {}.</summary>
public sealed record HeartbeatBody(int? PulseAgeMs = null);

public sealed record CommandBody(string Id, string Action, JsonElement? Args = null);

public sealed record ResultBody(string Id, bool Ok, string? Error = null);

public sealed record ByeBody(string? Reason = null);

public static class Wire
{
    public const int Version = 1;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Frame<T>(string type, long seq, Source? src, T body) =>
        JsonSerializer.Serialize(new Envelope(Version, type, seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), src,
            JsonSerializer.SerializeToElement(body, Json)), Json);

    public static Envelope? Parse(string text) => JsonSerializer.Deserialize<Envelope>(text, Json);

    public static T? Body<T>(this Envelope e) => e.Body.ValueKind == JsonValueKind.Undefined ? default : e.Body.Deserialize<T>(Json);
}
