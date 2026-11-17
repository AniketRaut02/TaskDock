using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging;
using TaskDock.Core.Dock;

namespace TaskDock.Remote;

/// <summary>
/// One notification for the phone. Says what needs you and where, never the detail line, a command, a path or a file name.
/// A newer message with the same tag replaces the older one on the phone.
/// </summary>
public sealed record PushMessageFor(PushKind Kind, string Title, string Body, string Tag, string? ApprovalId = null, DateTimeOffset? Until = null);

/// <summary>
/// Encrypted Web Push (VAPID) to paired phones. A queue of 64 that drops the oldest non-approval when full, one sender.
/// 404/410 deletes the subscription; 429 waits as Retry-After asks (the library does that).
/// </summary>
sealed class PushSender : IAsyncDisposable
{
    const int Capacity = 64;
    readonly LinkedList<(Device Device, PushMessageFor Message, PushUrgency Urgency)> _queue = new();
    readonly SemaphoreSlim _ready = new(0);
    readonly CancellationTokenSource _stop = new();
    readonly DeviceStore _devices;
    readonly TimeProvider _time;
    readonly ILogger _log;
    readonly PushServiceClient _client;
    readonly Task _worker;

    public PushSender(DeviceStore devices, TimeProvider time, ILogger log, HttpMessageHandler? handler = null)
    {
        (_devices, _time, _log) = (devices, time, log);
        var (pub, priv) = devices.Vapid();
        _client = new PushServiceClient(handler == null ? new HttpClient() : new HttpClient(handler))
        {
            DefaultAuthentication = new VapidAuthentication(pub, priv) { Subject = "https://github.com/AniketRaut02/TaskDock" },
            AutoRetryAfter = true, MaxRetriesAfter = 1,
        };
        _worker = Task.Run(Run);
    }

    public void Enqueue(Device device, PushMessageFor message, PushUrgency urgency)
    {
        lock (_queue)
        {
            if (_queue.Count >= Capacity)
            {
                var oldest = _queue.First;
                while (oldest != null && oldest.Value.Message.Kind == PushKind.Approval) oldest = oldest.Next;
                if (oldest == null && message.Kind != PushKind.Approval) return; // full of approvals: they matter more
                _queue.Remove(oldest ?? _queue.First!);
            }
            _queue.AddLast((device, message, urgency));
        }
        _ready.Release();
    }

    async Task Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await _ready.WaitAsync(_stop.Token); } catch (OperationCanceledException) { return; }
            (Device Device, PushMessageFor Message, PushUrgency Urgency) next;
            lock (_queue)
            {
                if (_queue.First is not { } first) continue;
                next = first.Value;
                _queue.RemoveFirst();
            }
            await Send(next.Device, next.Message, next.Urgency);
        }
    }

    async Task Send(Device device, PushMessageFor m, PushUrgency urgency)
    {
        if (device.Push is not { } target) return;
        var ttl = m.Until is { } until ? (int)Math.Max(0, (until - _time.GetUtcNow()).TotalSeconds) : 3600;
        if (ttl == 0) return; // the approval is already past its deadline
        var sub = new PushSubscription { Endpoint = target.Endpoint };
        sub.SetKey(PushEncryptionKeyName.P256DH, target.P256dh);
        sub.SetKey(PushEncryptionKeyName.Auth, target.Auth);
        var payload = JsonSerializer.Serialize(new { title = m.Title, body = m.Body, tag = m.Tag, approvalId = m.ApprovalId }, DockJson.Options);
        try
        {
            await _client.RequestPushMessageDeliveryAsync(sub, new PushMessage(payload)
            {
                TimeToLive = ttl, Urgency = urgency == PushUrgency.High ? PushMessageUrgency.High : PushMessageUrgency.Normal,
            }, _stop.Token);
        }
        catch (PushServiceClientException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            _log.LogInformation("Push subscription of {Device} expired; removed", device.Name);
            _devices.SetPush(device, null);
        }
        catch (Exception e) when (e is PushServiceClientException or HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Push to {Device} failed: {Error}", device.Name, e.Message); // never the payload
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _worker;
    }
}
