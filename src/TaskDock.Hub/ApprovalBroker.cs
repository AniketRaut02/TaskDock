using System.Collections.Concurrent;
using System.Text.Json;

namespace TaskDock.Hub;

public enum ApprovalChoice { Allow, AlwaysAllow, Deny, Terminal }

public sealed record PendingApproval(
    string Id, string SessionId, string ToolName, string? Preview, string ToolInputJson,
    JsonElement? Suggestions, DateTimeOffset Expires, DateTimeOffset HeldAt)
{
    public bool CanAlwaysAllow => Suggestions is { ValueKind: JsonValueKind.Array } s && s.GetArrayLength() > 0;
}

/// <summary>
/// Holds PermissionRequest calls open until the widget decides. Resolve is an in-process call: no hub route can set a
/// decision. TaskDock Remote reaches it only through DockHost, after its own checks (tailnet identity, device token, and a
/// fingerprint bound to the exact approval, input and choice); see the Security model in the Remote execution plan.
/// </summary>
public sealed class ApprovalBroker
{
    readonly ConcurrentDictionary<string, (PendingApproval Item, TaskCompletionSource<(ApprovalChoice, string?)> Tcs)> _held = new();

    /// <summary>Raised on a thread-pool thread whenever the pending set changes.</summary>
    public event Action? Changed;

    public IReadOnlyList<PendingApproval> Pending =>
        _held.Values.Select(v => v.Item).OrderBy(p => p.Expires).ToList();

    public bool Resolve(string id, ApprovalChoice choice, string? reason = null) =>
        _held.TryGetValue(id, out var h) && h.Tcs.TrySetResult((choice, reason));

    /// <summary>Resolves every approval this session is holding; returns how many.</summary>
    public int ResolveSession(string sessionId, ApprovalChoice choice) =>
        _held.Values.Where(h => h.Item.SessionId == sessionId).Count(h => h.Tcs.TrySetResult((choice, null)));

    /// <summary>Returns null when the hold window expires or Claude Code drops the request.</summary>
    internal async Task<(ApprovalChoice Choice, string? Reason)?> Hold(PendingApproval item, TimeSpan window, CancellationToken aborted)
    {
        var tcs = new TaskCompletionSource<(ApprovalChoice, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _held[item.Id] = (item, tcs);
        Changed?.Invoke();
        try { return await tcs.Task.WaitAsync(window, aborted); }
        catch (TimeoutException) { return null; }
        catch (OperationCanceledException) { return null; }
        finally
        {
            _held.TryRemove(item.Id, out _);
            Changed?.Invoke();
        }
    }
}
