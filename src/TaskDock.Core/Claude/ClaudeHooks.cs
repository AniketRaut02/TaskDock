using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskDock.Core.Claude;

/// <summary>
/// Pure merge logic for the hooks block in ~/.claude/settings.json. The App does the file I/O and backup.
/// Our entries are recognised by URL, so Disconnect removes only TaskDock's hooks.
/// </summary>
public static partial class ClaudeHooks
{
    public const string TokenHeader = "X-TaskDock-Token";

    public static readonly (string Event, string Route)[] Events =
    [
        ("SessionStart", "session-start"), ("UserPromptSubmit", "prompt"), ("PreToolUse", "pre-tool"),
        ("PermissionRequest", "permission"), ("Notification", "notify"), ("Stop", "stop"),
        ("StopFailure", "stop-failure"), ("SubagentStart", "subagent"), ("SubagentStop", "subagent"),
        ("SessionEnd", "session-end"),
    ];

    [GeneratedRegex(@"^http://127\.0\.0\.1:\d+/hooks/claude/")]
    private static partial Regex OurUrl();

    static bool IsOurs(JsonNode? hook) =>
        hook?["type"]?.GetValue<string>() == "http" && hook["url"]?.GetValue<string>() is { } u && OurUrl().IsMatch(u);

    public static bool IsInstalled(JsonObject settings) =>
        settings["hooks"] is JsonObject hooks && hooks.Any(kv =>
            kv.Value is JsonArray groups && groups.Any(g => g?["hooks"] is JsonArray hs && hs.Any(IsOurs)));

    /// <summary>Idempotent: removes any previous TaskDock entries, then adds the current ones.</summary>
    public static JsonObject Install(JsonObject settings, int port, string token, int holdSeconds)
    {
        Uninstall(settings);
        if (settings["hooks"] is not JsonObject hooks) settings["hooks"] = hooks = new JsonObject();
        foreach (var (evt, route) in Events)
        {
            var hook = new JsonObject
            {
                ["type"] = "http",
                ["url"] = $"http://127.0.0.1:{port}/hooks/claude/{route}",
                ["headers"] = new JsonObject { [TokenHeader] = token },
                // The hub answers at the hold window; the hook timeout must be longer so ours wins.
                ["timeout"] = evt == "PermissionRequest" ? holdSeconds + 10 : 5,
            };
            if (hooks[evt] is not JsonArray groups) hooks[evt] = groups = new JsonArray();
            groups.Add(new JsonObject { ["hooks"] = new JsonArray(hook) });
        }
        return settings;
    }

    public static JsonObject Uninstall(JsonObject settings)
    {
        if (settings["hooks"] is not JsonObject hooks) return settings;
        foreach (var evt in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[evt] is not JsonArray groups) continue;
            for (var i = groups.Count - 1; i >= 0; i--)
            {
                if (groups[i]?["hooks"] is not JsonArray hs) continue;
                for (var j = hs.Count - 1; j >= 0; j--) if (IsOurs(hs[j])) hs.RemoveAt(j);
                if (hs.Count == 0) groups.RemoveAt(i);
            }
            if (groups.Count == 0) hooks.Remove(evt);
        }
        if (hooks.Count == 0) settings.Remove("hooks");
        return settings;
    }

    /// <summary>Port and token our installed entries point at, so startup can rewrite them if either changed.</summary>
    public static (int Port, string? Token)? Installed(JsonObject settings)
    {
        if (settings["hooks"] is not JsonObject hooks) return null;
        foreach (var kv in hooks)
            if (kv.Value is JsonArray groups)
                foreach (var g in groups)
                    if (g?["hooks"] is JsonArray hs)
                        foreach (var h in hs.Where(IsOurs))
                        {
                            var uri = new Uri(h!["url"]!.GetValue<string>());
                            return (uri.Port, h["headers"]?[TokenHeader]?.GetValue<string>());
                        }
        return null;
    }
}
