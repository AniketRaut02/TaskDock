using System.Formats.Cbor;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using TaskDock.Core.Dock;

namespace TaskDock.Remote.Tests;

sealed class FakeHost : IDockHost
{
    public DockSnapshot? Snapshot { get; private set; }
    public event Action<DockSnapshot>? Changed;
    /// <summary>The PC's answer to "Allow … to control TaskDock?". Null: never answers.</summary>
    public bool? Confirm { get; set; } = true;
    public List<string> Asked { get; } = [];

    public void Publish(DockSnapshot s) { Snapshot = s; Changed?.Invoke(s); }

    public async Task<bool> ConfirmPairingAsync(string deviceName, CancellationToken ct)
    {
        Asked.Add(deviceName);
        if (Confirm is { } yes) return yes;
        await Task.Delay(Timeout.Infinite, ct);
        return false;
    }

    public List<ApprovalInput> Approvals { get; } =
    [
        new("bash1", "s1", "Bash", "npm test", """{"command":"npm test"}""", true, default, default),
        new("bash2", "s1", "Bash", "rm -rf dist", """{"command":"rm -rf dist"}""", true, default, default),
        new("grep1", "s1", "Grep", "TODO", """{"pattern":"TODO"}""", false, default, default),
    ];
    public List<DockAction> Ran { get; } = [];
    public List<string> Notices { get; } = [];

    public ApprovalInput? Approval(string id) => Approvals.FirstOrDefault(a => a.Id == id);
    public Requirement Requirement(DockAction action) => action switch
    {
        ApprovalAlwaysAllow => Core.Dock.Requirement.Fingerprint,
        ApprovalAllow a => Approval(a.ApprovalId) is { } p && Core.Claude.ToolRisk.Classify(p.ToolName) == Core.Claude.Risk.Read ? Core.Dock.Requirement.None : Core.Dock.Requirement.Fingerprint,
        _ => Core.Dock.Requirement.None,
    };
    public Task<DockResult> RunAsync(DockAction action) { lock (Ran) Ran.Add(action); return Task.FromResult(DockResult.Success); }
    public void Notice(string text) { lock (Notices) Notices.Add(text); }
}

/// <summary>A RemoteServer on a random loopback port, reached under the tailnet name like tailscale serve does.</summary>
sealed class Harness : IAsyncDisposable
{
    public const string Name = "pc.tail1234.ts.net";
    public const string Owner = "me@example.com";

    public FakeHost Host { get; } = new();
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-11-16T12:00:00Z"));
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "taskdock-remote-" + Guid.NewGuid().ToString("N"));
    public RemoteServer Server { get; }

    public Harness(TimeSpan? confirmTimeout = null)
    {
        Server = new RemoteServer(new RemoteOptions { BasePort = 0, HostName = Name, Time = Time, DataDir = Dir, ConfirmTimeout = confirmTimeout ?? TimeSpan.FromMinutes(2) },
            Host, new DeviceStore(Dir));
    }

    public static async Task<Harness> Start(TimeSpan? confirmTimeout = null)
    {
        var h = new Harness(confirmTimeout);
        h.Host.Publish(Snapshots.Base());
        await h.Server.StartAsync();
        return h;
    }

    SocketsHttpHandler Handler() => new()
    {
        ConnectCallback = async (_, ct) =>
        {
            var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await s.ConnectAsync(IPAddress.Loopback, Server.Port, ct);
            return new NetworkStream(s, true);
        },
    };

    /// <summary>As if through Serve: Host is the bare tailnet name and the identity header is set.</summary>
    public HttpClient Client(string? login = Owner, string? token = null)
    {
        var c = new HttpClient(Handler()) { BaseAddress = new Uri($"http://{Name}/") };
        if (login != null) c.DefaultRequestHeaders.Add("Tailscale-User-Login", login);
        if (token != null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public async Task<ClientWebSocket> Socket(string? login = Owner)
    {
        var ws = new ClientWebSocket();
        if (login != null) ws.Options.SetRequestHeader("Tailscale-User-Login", login);
        await ws.ConnectAsync(new Uri($"ws://{Name}/api/live"), new HttpMessageInvoker(Handler()), CancellationToken.None);
        return ws;
    }

    /// <summary>Full pairing as a phone does it. Returns the device's token.</summary>
    public SoftAuthenticator Phone { get; private set; } = new();

    public async Task<(string Token, string DeviceId, HttpResponseMessage Finish)> Pair(string name = "Pixel 8", string login = Owner)
    {
        Phone = new SoftAuthenticator();
        var code = Server.NewPairingCode();
        using var c = Client(login);
        var start = await c.PostAsync("api/pair/start", Json(new JsonObject { ["code"] = code, ["deviceName"] = name }));
        start.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await start.Content.ReadAsStringAsync())!;
        var token = body["token"]!.GetValue<string>();
        using var c2 = Client(login, token);
        var finish = await c2.PostAsync("api/pair/finish", Json(Phone.Register(body["creationOptions"]!, $"https://{Name}", Name)));
        return (token, body["deviceId"]!.GetValue<string>(), finish);
    }

    public static StringContent Json(JsonNode n) => new(n.ToJsonString(), Encoding.UTF8, "application/json");

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

/// <summary>A platform authenticator in software: P-256, "none" attestation, user verified. Keeps its key so it can sign.</summary>
sealed class SoftAuthenticator
{
    readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly byte[] _credId = RandomNumberGenerator.GetBytes(16);
    uint _count;

    public static JsonObject Create(JsonNode creationOptions, string origin, string rpId) => new SoftAuthenticator().Register(creationOptions, origin, rpId);

    /// <summary>A WebAuthn assertion over the server's options. counter: null keeps counting up (0 = never counts, like Google Password Manager).</summary>
    public JsonObject Assert(JsonNode assertionOptions, string origin, string rpId, bool userVerified = true, uint? counter = null)
    {
        var count = counter ?? ++_count;
        byte[] authData = [.. SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), (byte)(userVerified ? 0x05 : 0x01),
            (byte)(count >> 24), (byte)(count >> 16), (byte)(count >> 8), (byte)count];
        var clientData = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["type"] = "webauthn.get", ["challenge"] = assertionOptions["challenge"]!.GetValue<string>(), ["origin"] = origin, ["crossOrigin"] = false,
        }.ToJsonString());
        var sig = _key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var id = DeviceStore.Base64Url(_credId);
        return new JsonObject
        {
            ["id"] = id, ["rawId"] = id, ["type"] = "public-key", ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["authenticatorData"] = DeviceStore.Base64Url(authData),
                ["clientDataJSON"] = DeviceStore.Base64Url(clientData),
                ["signature"] = DeviceStore.Base64Url(sig),
            },
        };
    }

    public JsonObject Register(JsonNode creationOptions, string origin, string rpId)
    {
        var key = _key;
        var p = key.ExportParameters(false);
        var credId = _credId;

        var cose = new CborWriter(CborConformanceMode.Ctap2Canonical);
        cose.WriteStartMap(5);
        cose.WriteInt32(1); cose.WriteInt32(2);     // kty: EC2
        cose.WriteInt32(3); cose.WriteInt32(-7);    // alg: ES256
        cose.WriteInt32(-1); cose.WriteInt32(1);    // crv: P-256
        cose.WriteInt32(-2); cose.WriteByteString(p.Q.X);
        cose.WriteInt32(-3); cose.WriteByteString(p.Q.Y);
        cose.WriteEndMap();

        var authData = new List<byte>();
        authData.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        authData.Add(0x45); // UP | UV | AT
        authData.AddRange([0, 0, 0, 0]);
        authData.AddRange(new byte[16]); // AAGUID
        authData.AddRange([(byte)(credId.Length >> 8), (byte)credId.Length]);
        authData.AddRange(credId);
        authData.AddRange(cose.Encode());

        var att = new CborWriter(CborConformanceMode.Ctap2Canonical);
        att.WriteStartMap(3);
        att.WriteTextString("fmt"); att.WriteTextString("none");
        att.WriteTextString("attStmt"); att.WriteStartMap(0); att.WriteEndMap();
        att.WriteTextString("authData"); att.WriteByteString(authData.ToArray());
        att.WriteEndMap();

        var clientData = new JsonObject
        {
            ["type"] = "webauthn.create", ["challenge"] = creationOptions["challenge"]!.GetValue<string>(), ["origin"] = origin, ["crossOrigin"] = false,
        };
        var id = DeviceStore.Base64Url(credId);
        return new JsonObject
        {
            ["id"] = id, ["rawId"] = id, ["type"] = "public-key", ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["attestationObject"] = DeviceStore.Base64Url(att.Encode()),
                ["clientDataJSON"] = DeviceStore.Base64Url(Encoding.UTF8.GetBytes(clientData.ToJsonString())),
            },
        };
    }
}

static class Snapshots
{
    static readonly TileCaps NoCaps = new(false, false, false, "Pause", false, false, false, true, false, false);

    public static TileView Tile(string key, string state = "Idle", Core.Tiles.StatusClass c = Core.Tiles.StatusClass.Idle, string? label = null) =>
        new(key, null, key.Split('|')[0], label ?? key, state, c, null, null, false, false, NoCaps, null);

    public static DockSnapshot Base() => new("epoch1", 1, new PillView("All quiet", Core.Tiles.StatusClass.Idle),
        [Tile("unity|a"), Tile("vscode|b")], [Tile("claude|c")], [], null, new PresetsView("Default", ["Default"]), new FlagsView(false, false));
}
