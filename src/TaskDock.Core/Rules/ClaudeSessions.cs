using System.Text.Json;
using TaskDock.Core.Tiles;

namespace TaskDock.Core.Rules;

/// <summary>
/// Claude Code session state machine, fed by hook payloads. Not thread-safe; the hub serialises calls.
/// States: Idle, Working, Needs approval, Needs input, Done, Failed, Ended.
/// </summary>
public sealed class ClaudeSessions
{
    sealed class Session
    {
        public string Cwd = "";
        public string? Title;
        public string State = "Idle";
        public string? Detail;
        public int Subagents;
        public int Pending; // approvals the widget is holding
        public bool Seen;
    }

    readonly Dictionary<string, Session> _s = new();

    public StateEvent? Apply(JsonElement p)
    {
        var id = Str(p, "session_id");
        var evt = Str(p, "hook_event_name");
        if (id == null || evt == null) return null;

        if (!_s.TryGetValue(id, out var s)) _s[id] = s = new Session();
        var connected = !s.Seen;
        s.Seen = true;
        if (Str(p, "cwd") is { } cwd) s.Cwd = cwd;
        if (Str(p, "session_title") is { Length: > 0 } title) s.Title = title;

        switch (evt)
        {
            case "SessionStart": s.State = "Idle"; s.Detail = null; s.Subagents = 0; break;
            case "UserPromptSubmit": s.State = "Working"; s.Detail = null; break;
            case "PreToolUse":
                if (s.Pending > 0) return null; // a parallel tool call must not hide a held approval
                s.State = "Working";
                s.Detail = ToolLine(p);
                break;
            case "PermissionRequest": s.Pending++; s.State = "Needs approval"; s.Detail = ToolLine(p); break;
            case "Notification":
                switch (Str(p, "notification_type"))
                {
                    case "idle_prompt" or "elicitation_dialog": s.State = "Needs input"; s.Detail = OneLine(Str(p, "message")); break;
                    case "permission_prompt": s.State = "Needs approval"; s.Detail = "Waiting in terminal"; break;
                    default: return null;
                }
                break;
            case "Stop": s.Pending = 0; s.State = "Done"; s.Detail = OneLine(Str(p, "last_assistant_message")); s.Subagents = 0; break;
            case "StopFailure": s.State = "Failed"; s.Detail = OneLine(Str(p, "error") ?? Str(p, "error_type") ?? Str(p, "reason")); break;
            case "SubagentStart": s.Subagents++; s.Detail = SubagentLine(s.Subagents); break;
            case "SubagentStop": s.Subagents = Math.Max(0, s.Subagents - 1); s.Detail = SubagentLine(s.Subagents); break;
            case "SessionEnd": s.State = "Ended"; s.Detail = null; break;
            default: return null;
        }
        var e = Event(id, s, connected);
        if (evt == "SessionEnd") _s.Remove(id);
        return e;
    }

    /// <summary>After a widget decision the turn carries on.</summary>
    public StateEvent? Resumed(string sessionId)
    {
        if (!_s.TryGetValue(sessionId, out var s)) return null;
        s.Pending = Math.Max(0, s.Pending - 1);
        if (s.Pending > 0 || s.State != "Needs approval") return null;
        s.State = "Working";
        s.Detail = null;
        return Event(sessionId, s, false);
    }

    /// <summary>The widget let the terminal prompt take over (Terminal button or hold timeout).</summary>
    public StateEvent? HandedToTerminal(string sessionId)
    {
        if (!_s.TryGetValue(sessionId, out var s)) return null;
        s.Pending = Math.Max(0, s.Pending - 1);
        if (s.State != "Needs approval") return null;
        s.Detail = "Waiting in terminal";
        return Event(sessionId, s, false);
    }

    static StateEvent Event(string id, Session s, bool connected) => new(
        "claude", id, s.State, s.Title ?? FolderName(s.Cwd), s.Detail,
        Match: new Dictionary<string, string> { ["cwd"] = s.Cwd }, Connected: connected);

    static string? SubagentLine(int n) => n == 0 ? null : n == 1 ? "1 subagent" : $"{n} subagents";

    public static string FolderName(string cwd)
    {
        var t = cwd.TrimEnd('/', '\\');
        var i = t.LastIndexOfAny(['/', '\\']);
        return t.Length == 0 ? "Claude" : t[(i + 1)..];
    }

    public static string? ToolLine(JsonElement p)
    {
        var tool = Str(p, "tool_name");
        if (tool == null) return null;
        var preview = p.TryGetProperty("tool_input", out var ti) ? ToolPreview(ti) : null;
        return OneLine(preview == null ? tool : $"{tool}: {preview}");
    }

    /// <summary>The command, file path or URL, whichever the tool has.</summary>
    public static string? ToolPreview(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in new[] { "command", "file_path", "url", "path", "pattern", "query", "description" })
            if (Str(input, k) is { } v) return v;
        return null;
    }

    public static string? OneLine(string? s, int max = 90)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var line = s.Trim().Split('\n', 2)[0].Trim();
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
