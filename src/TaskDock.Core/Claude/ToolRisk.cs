namespace TaskDock.Core.Claude;

public enum Risk { Read, Risky }

/// <summary>Which Claude Code tools a phone may approve with one tap. Allowlist: anything unknown is risky.</summary>
public static class ToolRisk
{
    static readonly HashSet<string> ReadOnly = ["Read", "Grep", "Glob", "LS", "WebSearch", "TodoWrite"];

    public static Risk Classify(string? toolName) => toolName != null && ReadOnly.Contains(toolName) ? Risk.Read : Risk.Risky;
}
