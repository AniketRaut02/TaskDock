using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TaskDock.Core.Dock;

namespace TaskDock.Remote.Tests;

/// <summary>Stands in for FCM: records what TaskDock sends and answers with the given status.</summary>
sealed class FakePushService : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, byte[] Body)> Sent { get; } = [];
    public HttpStatusCode Answer { get; set; } = HttpStatusCode.Created;
    readonly SemaphoreSlim _got = new(0);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        lock (Sent) Sent.Add((request, body));
        _got.Release();
        return new HttpResponseMessage(Answer);
    }

    public Task<bool> Next(int ms = 3000) => _got.WaitAsync(ms);
}

public class PushTests
{
    const string Fcm = "https://fcm.googleapis.com/fcm/send/abc123";

    static JsonObject Subscription(string endpoint = Fcm)
    {
        using var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(false);
        return new JsonObject
        {
            ["endpoint"] = endpoint,
            ["keys"] = new JsonObject { ["p256dh"] = DeviceStore.Base64Url([0x04, .. p.Q.X!, .. p.Q.Y!]), ["auth"] = DeviceStore.Base64Url(RandomNumberGenerator.GetBytes(16)) },
        };
    }

    static async Task<(Harness H, FakePushService Push, HttpClient C, string Token)> Subscribed()
    {
        var push = new FakePushService();
        var h = await Harness.Start(pushHandler: push);
        var (token, _, _) = await h.Pair();
        var c = h.Client(token: token);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("api/push/subscribe", Harness.Json(Subscription()))).StatusCode);
        return (h, push, c, token);
    }

    static readonly PushMessageFor Approval = new(PushKind.Approval, "Claude needs approval", "Bash · api", "approval-a1", "a1", null);

    [Theory]
    [InlineData("https://evil.example.com/x")]
    [InlineData("https://127.0.0.1/fcm")]
    [InlineData("http://fcm.googleapis.com/fcm/send/x")]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/x")]
    [InlineData("https://fcm.googleapis.com.evil.com/x")]
    [InlineData("not a url")]
    public async Task Only_real_push_services_can_be_subscribed(string endpoint)
    {
        await using var h = await Harness.Start();
        var (token, _, _) = await h.Pair();
        var r = await h.Client(token: token).PostAsync("api/push/subscribe", Harness.Json(Subscription(endpoint)));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(h.Server.Devices.Devices.Single().Push);
    }

    [Fact]
    public async Task The_vapid_key_is_made_once_and_kept()
    {
        await using var h = await Harness.Start();
        var (token, _, _) = await h.Pair();
        var key = JsonNode.Parse(await h.Client(token: token).GetStringAsync("api/push/vapid"))!["key"]!.GetValue<string>();
        Assert.Equal(87, key.Length); // 65-byte uncompressed P-256 point, base64url
        Assert.Equal(key, new DeviceStore(h.Dir).Vapid().Public);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client().GetAsync("api/push/vapid")).StatusCode);
    }

    [Fact]
    public async Task Away_with_the_app_closed_sends_an_encrypted_high_urgency_push()
    {
        var (h, push, _, _) = await Subscribed();
        await using var _ = h;
        Assert.Equal(1, h.Server.Notify(Approval with { Until = h.Time.GetUtcNow().AddMinutes(9) }, away: true, quietHours: false));
        Assert.True(await push.Next());
        var (req, body) = push.Sent.Single();
        Assert.Equal(Fcm, req.RequestUri!.ToString());
        Assert.Equal("high", req.Headers.GetValues("Urgency").Single());
        Assert.InRange(int.Parse(req.Headers.GetValues("TTL").Single()), 530, 540); // until the approval's deadline
        Assert.StartsWith("vapid ", req.Headers.Authorization!.ToString());
        Assert.Equal("aes128gcm", req.Content!.Headers.ContentEncoding.Single());
        Assert.DoesNotContain("Claude", Encoding.UTF8.GetString(body)); // end-to-end encrypted: the push service can't read it
    }

    [Fact]
    public async Task At_the_pc_nothing_is_sent()
    {
        var (h, push, _, _) = await Subscribed();
        await using var _ = h;
        Assert.Equal(0, h.Server.Notify(Approval, away: false, quietHours: false));
        Assert.False(await push.Next(300));
    }

    [Fact]
    public async Task With_the_app_open_nothing_is_sent()
    {
        var (h, push, _, token) = await Subscribed();
        await using var _ = h;
        using var ws = await h.Socket();
        await ws.SendAsync(Encoding.UTF8.GetBytes(new JsonObject { ["t"] = "auth", ["body"] = new JsonObject { ["token"] = token } }.ToJsonString()),
            WebSocketMessageType.Text, true, CancellationToken.None);
        await ws.ReceiveAsync(new byte[64 * 1024], CancellationToken.None); // hello: the session is registered
        Assert.Equal(0, h.Server.Notify(Approval, away: true, quietHours: false));
    }

    [Fact]
    public async Task Quiet_hours_hold_back_everything_but_approvals()
    {
        var (h, push, _, _) = await Subscribed();
        await using var _ = h;
        Assert.Equal(0, h.Server.Notify(new PushMessageFor(PushKind.TileError, "MyGame", "Compile errors (2)", "tile-unity|a"), away: true, quietHours: true));
        Assert.Equal(1, h.Server.Notify(Approval, away: true, quietHours: true));
        Assert.True(await push.Next());
        var normal = await Send(h, push, new PushMessageFor(PushKind.TileError, "MyGame", "Failed", "tile-unity|a"));
        Assert.False(normal.Headers.Contains("Urgency")); // absent means normal (RFC 8030)
    }

    static async Task<HttpRequestMessage> Send(Harness h, FakePushService push, PushMessageFor m)
    {
        h.Server.Notify(m, away: true, quietHours: false);
        Assert.True(await push.Next());
        return push.Sent[^1].Request;
    }

    [Fact]
    public async Task An_expired_subscription_is_deleted()
    {
        var (h, push, _, _) = await Subscribed();
        await using var _ = h;
        push.Answer = HttpStatusCode.Gone;
        h.Server.Notify(Approval, away: true, quietHours: false);
        Assert.True(await push.Next());
        for (var i = 0; i < 50 && h.Server.Devices.Devices.Single().Push != null; i++) await Task.Delay(20);
        Assert.Null(h.Server.Devices.Devices.Single().Push);
        Assert.Null(new DeviceStore(h.Dir).Devices.Single().Push); // and it's gone from disk
    }
}

public class HostNameTests
{
    [Fact]
    public async Task Renaming_the_pc_drops_phones_whose_passkeys_no_longer_fit()
    {
        var h = await Harness.Start();
        var (token, _, _) = await h.Pair();
        Assert.False(h.Server.HostChanged); // the first start just records the name
        await h.Server.DisposeAsync();

        var same = new DeviceStore(h.Dir);
        Assert.False(same.UseHost(Harness.Name.ToUpperInvariant()));
        Assert.NotNull(same.ByToken(token));

        var renamed = new DeviceStore(h.Dir);
        Assert.True(renamed.UseHost("newpc.tail1234.ts.net"));
        Assert.Empty(renamed.Devices);
        Assert.Empty(new DeviceStore(h.Dir).Devices); // saved
        Assert.False(new DeviceStore(h.Dir).UseHost("newpc.tail1234.ts.net"));
        Directory.Delete(h.Dir, true);
    }
}
