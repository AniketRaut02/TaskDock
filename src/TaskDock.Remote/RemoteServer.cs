using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskDock.Core.Dock;

namespace TaskDock.Remote;

public sealed class RemoteOptions
{
    /// <summary>47830, falling back through 47839. 0 lets the OS pick (tests).</summary>
    public int BasePort { get; init; } = 47830;
    public int PortSpan { get; init; } = 10;
    public string DataDir { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskDock", "remote");
    /// <summary>This PC's tailnet name, e.g. pc.tailnet.ts.net (no trailing dot): the only public Host, Origin and passkey RP ID.</summary>
    public required string HostName { get; init; }
    /// <summary>Serve's HTTPS port: 443, or 8443 when something else already uses 443 on this PC.</summary>
    public int HttpsPort { get; init; } = 443;
    /// <summary>What the phone puts in Host and Origin: the bare name on 443, name:port otherwise.</summary>
    public string PublicHost => HttpsPort == 443 ? HostName : $"{HostName}:{HttpsPort}";
    public string ServerVersion { get; init; } = "0.2.0";
    public Action<ILoggingBuilder>? Logging { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>How long a phone waits for the PC's yes before its pairing is dropped.</summary>
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Tests: where Web Push requests go instead of the internet.</summary>
    public HttpMessageHandler? PushHandler { get; init; }
}

/// <summary>
/// The phone's only door: Kestrel on 127.0.0.1, reached from the tailnet through `tailscale serve`. Separate from the
/// hub on purpose: per-device credentials, browser clients and a public TLS name must not weaken the hub's trust model.
/// </summary>
public sealed class RemoteServer(RemoteOptions options, IDockHost host, DeviceStore devices) : IAsyncDisposable
{
    const int MaxClientFrame = 16 * 1024;
    const int MaxBody = 64 * 1024;
    const string IdentityHeader = "Tailscale-User-Login";
    const string Csp = "default-src 'self'; img-src 'self' blob:; connect-src 'self'; script-src 'self'; style-src 'self'";

    readonly ConcurrentDictionary<LiveSession, byte> _sessions = new();
    readonly ConcurrentDictionary<string, string> _pendingOptions = new(); // device id → CredentialCreateOptions JSON
    readonly Lock _codeLock = new();
    PairingCode? _code;
    WebApplication? _app;
    RemoteActions? _actions;
    PushSender? _push;

    public ActivityLog Activity { get; } = new(options.DataDir, options.Time);

    sealed class PairingCode(string value, DateTimeOffset expires)
    {
        public string Value { get; } = value;
        public DateTimeOffset Expires { get; } = expires;
        public int Failures { get; set; }
    }

    public int Port { get; private set; }
    public DeviceStore Devices => devices;

    public async Task StartAsync()
    {
        var ports = options.BasePort == 0 ? [0] : Enumerable.Range(options.BasePort, options.PortSpan);
        foreach (var port in ports)
        {
            var app = Build(port);
            try { await app.StartAsync(); }
            catch (IOException) { await app.DisposeAsync(); continue; } // port taken: try the next
            _app = app;
            Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
            host.Changed += OnSnapshot;
            _push = new PushSender(devices, options.Time, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TaskDock.Remote"), options.PushHandler);
            return;
        }
        throw new IOException($"No free port in {options.BasePort}-{options.BasePort + options.PortSpan - 1}");
    }

    public async ValueTask DisposeAsync()
    {
        host.Changed -= OnSnapshot;
        foreach (var s in _sessions.Keys) s.Abort();
        if (_push != null) await _push.DisposeAsync();
        if (_app != null) await _app.DisposeAsync();
    }

    /// <summary>
    /// Sends m to each paired phone whose push policy says so: only while nobody is at the PC (away) and that phone's app
    /// isn't open. Returns how many phones it was queued for.
    /// </summary>
    public int Notify(PushMessageFor m, bool away, bool quietHours)
    {
        if (_push == null) return 0;
        var n = 0;
        foreach (var d in devices.Devices.Where(d => d.IsActive && d.Push != null))
        {
            var appOpen = _sessions.Keys.Any(s => s.Device.Id == d.Id);
            if (PushPolicy.Decide(m.Kind, away, appOpen, quietHours) is var u && u == PushUrgency.None) continue;
            _push.Enqueue(d, m, u);
            n++;
        }
        return n;
    }

    /// <summary>Whether this phone has the app open right now (a live socket).</summary>
    public bool AnyPhoneOpen => !_sessions.IsEmpty;

    // Web Push endpoints a phone may register. Anything else could make this PC POST to an address a token thief picked.
    static readonly string[] PushHosts = ["fcm.googleapis.com", ".push.services.mozilla.com", ".notify.windows.com", "web.push.apple.com"];

    static bool IsPushEndpoint(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.IsDefaultPort
        && PushHosts.Any(h => h.StartsWith('.') ? u.Host.EndsWith(h, StringComparison.OrdinalIgnoreCase) : u.Host.Equals(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>A new single-use pairing code, valid 5 minutes, 5 wrong attempts. Replaces any earlier one.</summary>
    public string NewPairingCode()
    {
        var value = DeviceStore.Base64Url(RandomNumberGenerator.GetBytes(16));
        lock (_codeLock) _code = new PairingCode(value, options.Time.GetUtcNow() + TimeSpan.FromMinutes(5));
        return value;
    }

    public string PairingUrl(string code) => $"https://{options.PublicHost}/pair#c={code}";

    /// <summary>Deletes the device (token hash, passkey) and drops its open sockets at once.</summary>
    public bool Revoke(string deviceId)
    {
        foreach (var s in _sessions.Keys.Where(s => s.Device.Id == deviceId)) s.Abort();
        _pendingOptions.TryRemove(deviceId, out _);
        return devices.Remove(deviceId);
    }

    void OnSnapshot(DockSnapshot s)
    {
        foreach (var session in _sessions.Keys) session.Notify();
    }

    WebApplication Build(int port)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        options.Logging?.Invoke(b.Logging);
        b.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, port); // check 1: loopback only; tailscale serve is the way in
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = MaxBody;
        });
        var app = b.Build();

        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h.ContentSecurityPolicy = Csp;
            h.XContentTypeOptions = "nosniff";
            h["Referrer-Policy"] = "no-referrer";

            // 2. DNS rebinding: our loopback address or the tailnet name (Serve sends it without a port).
            var hostHeader = ctx.Request.Host.Value;
            if (hostHeader != $"127.0.0.1:{Port}" && !string.Equals(hostHeader, options.PublicHost, StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 403; return; }
            // 3. Came through Serve, which sets this and strips any copy the client sent.
            var login = ctx.Request.Headers[IdentityHeader].ToString();
            if (login.Length == 0) { ctx.Response.StatusCode = 403; return; }
            // 4. The owner's tailnet account only (anyone may reach the pairing page until the first phone is paired).
            if (devices.OwnerLogin is { } owner && !string.Equals(login, owner, StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 403; return; }
            // 6. Never answer preflights or send CORS headers; a browser Origin must be our own.
            if (HttpMethods.IsOptions(ctx.Request.Method)) { ctx.Response.StatusCode = 403; return; }
            var origin = ctx.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !string.Equals(origin, $"https://{options.PublicHost}", StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 403; return; }
            await next();
        });
        // KeepAliveTimeout: a phone that stops answering pings (lost network, Doze) is dropped instead of lingering half-open.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(30) });

        // 5. Every /api route except pairing start and the socket (which authenticates with its first frame) needs a token.
        // (Delegate): as a bare method group these bind as RequestDelegate and their IResult is silently dropped.
        app.MapPost("/api/pair/start", (Delegate)PairStart);
        app.MapPost("/api/pair/finish", (Delegate)PairFinish);
        app.Map("/api/live", Live);
        app.MapDelete("/api/devices/self", (HttpContext ctx) =>
            Bearer(ctx) is { IsActive: true } d ? (Revoke(d.Id) ? Results.Ok() : Results.NotFound()) : Results.StatusCode(401));
        // The service worker's Deny (Phase 4) and any client without a socket: same body and result as an action frame.
        app.MapPost("/api/action", (Delegate)(async (HttpContext ctx) =>
            Bearer(ctx) is { IsActive: true } d ? Json(await Actions.Handle(d, await ReadJson(ctx) as JsonObject)) : Results.StatusCode(401)));
        app.MapPost("/api/stepup/options", (Delegate)(async (HttpContext ctx) =>
        {
            if (Bearer(ctx) is not { IsActive: true } d) return Results.StatusCode(401);
            var (o, code) = Actions.StepUpOptions(d, await ReadJson(ctx) as JsonObject);
            return o != null ? Results.Content(o, "application/json")
                : Results.Json(new { code }, statusCode: code switch { "rate_limited" => 429, "already_resolved" => 409, _ => 400 });
        }));
        app.MapPost("/api/peek/grant", (Delegate)(async (HttpContext ctx) =>
        {
            if (Bearer(ctx) is not { IsActive: true } d) return Results.StatusCode(401);
            var assertion = (await ReadJson(ctx))?["assertion"];
            var ok = assertion is JsonObject && await Actions.GrantPeek(d, JsonSerializer.Deserialize<JsonElement>(assertion.ToJsonString()));
            return ok ? Results.Ok(new { ok = true }) : Results.Json(new { code = "stepup_failed" }, statusCode: 403);
        }));
        app.MapGet("/api/peek/{tileKey}", (Delegate)(async (HttpContext ctx, string tileKey) =>
        {
            if (Bearer(ctx) is not { IsActive: true } d) return Results.StatusCode(401);
            var (jpeg, code) = await Actions.Peek(d, tileKey);
            ctx.Response.Headers.CacheControl = "no-store";
            if (jpeg != null) return Results.Bytes(jpeg, "image/jpeg");
            return Results.Json(new { code }, statusCode: code switch
            {
                "stepup_required" => 403, "rate_limited" => 429, "not_found" => 404, _ => 409, // minimized, blank
            });
        }));
        app.MapGet("/api/push/vapid", (HttpContext ctx) =>
            Bearer(ctx) is { IsActive: true } ? Results.Json(new { key = devices.Vapid().Public }) : Results.StatusCode(401));
        app.MapPost("/api/push/subscribe", (Delegate)(async (HttpContext ctx) =>
        {
            if (Bearer(ctx) is not { IsActive: true } d) return Results.StatusCode(401);
            var b = await ReadJson(ctx);
            string? S(JsonNode? n) => n?.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;
            var (endpoint, p256dh, auth) = (S(b?["endpoint"]), S(b?["keys"]?["p256dh"]), S(b?["keys"]?["auth"]));
            if (!IsPushEndpoint(endpoint) || endpoint!.Length > 1024 || p256dh is not { Length: > 0 and <= 200 } || auth is not { Length: > 0 and <= 100 })
                return Results.BadRequest(new { code = "invalid" });
            devices.SetPush(d, new PushTarget(endpoint, p256dh, auth));
            return Results.Ok(new { ok = true });
        }));
        app.MapGet("/api/activity", (HttpContext ctx) =>
        {
            if (Bearer(ctx) is not { IsActive: true }) return Results.StatusCode(401);
            DateTimeOffset? before = long.TryParse(ctx.Request.Query["before"], out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            return Results.Json(Activity.Page(before), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        });
        app.MapFallback("{*path}", Static); // the default fallback pattern skips file-like paths (app.js, icons)
        return app;
    }

    RemoteActions Actions => _actions ??= new RemoteActions(host, new StepUp(Fido, devices, options.Time), Activity, options.Time);

    static IResult Json(JsonObject o) => Results.Content(o.ToJsonString(), "application/json");

    Device? Bearer(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.Ordinal) ? devices.ByToken(auth[7..]) : null;
    }

    // ---------- pairing ----------

    IFido2 Fido() => new Fido2(new Fido2Configuration
    {
        RPID = options.HostName, RPName = "TaskDock", Origins = new HashSet<string> { $"https://{options.PublicHost}" },
    });

    async Task<IResult> PairStart(HttpContext ctx)
    {
        var body = await ReadJson(ctx);
        var code = body?["code"]?.GetValueKind() == JsonValueKind.String ? body["code"]!.GetValue<string>() : "";
        var name = body?["deviceName"]?.GetValueKind() == JsonValueKind.String ? body["deviceName"]!.GetValue<string>().Trim() : "";
        if (name.Length is 0 or > 40) return Results.BadRequest(new { error = "deviceName must be 1 to 40 characters" });

        lock (_codeLock)
        {
            if (_code == null || options.Time.GetUtcNow() > _code.Expires) { _code = null; return Results.Json(new { error = "expired" }, statusCode: 403); }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(_code.Value)))
            {
                if (++_code.Failures >= 5) _code = null;
                return Results.Json(new { error = "wrong code" }, statusCode: 403);
            }
            _code = null; // single use
        }

        var (device, token) = devices.AddPending(name, ctx.Request.Headers[IdentityHeader].ToString(), options.Time.GetUtcNow());
        var create = Fido().RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = Encoding.UTF8.GetBytes(device.Id), Name = name, DisplayName = name },
            ExcludeCredentials = [],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                AuthenticatorAttachment = AuthenticatorAttachment.Platform,
                UserVerification = UserVerificationRequirement.Required,
                ResidentKey = ResidentKeyRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        _pendingOptions[device.Id] = create.ToJson();
        return Results.Content(new JsonObject
        {
            ["deviceId"] = device.Id, ["token"] = token, ["creationOptions"] = JsonNode.Parse(create.ToJson()),
        }.ToJsonString(), "application/json");
    }

    async Task<IResult> PairFinish(HttpContext ctx)
    {
        if (Bearer(ctx) is not { IsActive: false } device || !_pendingOptions.TryRemove(device.Id, out var optionsJson)) return Results.StatusCode(401);
        try
        {
            var attestation = await JsonSerializer.DeserializeAsync<AuthenticatorAttestationRawResponse>(ctx.Request.Body, cancellationToken: ctx.RequestAborted)
                ?? throw new JsonException("empty");
            var cred = await Fido().MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestation,
                OriginalOptions = CredentialCreateOptions.FromJson(optionsJson),
                IsCredentialIdUniqueToUserCallback = (_, _) => Task.FromResult(true),
            }, ctx.RequestAborted);

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            wait.CancelAfter(options.ConfirmTimeout);
            bool yes;
            try { yes = await host.ConfirmPairingAsync(device.Name, wait.Token); }
            catch (OperationCanceledException) { yes = false; }
            if (!yes) { devices.Remove(device.Id); return Results.Json(new { error = "declined on the PC" }, statusCode: 403); }

            devices.Activate(device, new StoredCredential
            {
                Id = DeviceStore.Base64Url(cred.Id), PublicKey = DeviceStore.Base64Url(cred.PublicKey), SignCount = cred.SignCount,
            });
            return Results.Ok(new { ok = true });
        }
        catch (Exception e) when (e is JsonException or Fido2VerificationException)
        {
            devices.Remove(device.Id);
            return Results.Json(new { error = "passkey not accepted" }, statusCode: 400);
        }
    }

    static async Task<JsonNode?> ReadJson(HttpContext ctx)
    {
        try { return await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
        catch (JsonException) { return null; }
    }

    // ---------- live socket ----------

    async Task Live(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

        // Race a timer rather than cancel the receive: a cancelled receive aborts the socket, and the phone should see a clean 4401.
        var receive = LiveSession.Receive(ws, MaxClientFrame, ctx.RequestAborted);
        var first = await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(5), ctx.RequestAborted)) == receive ? await receive : null;
        var token = first == null ? null : Frame(first) is { T: "auth" } f ? f.Body?["token"]?.GetValueKind() == JsonValueKind.String ? f.Body["token"]!.GetValue<string>() : null : null;
        if (devices.ByToken(token) is not { IsActive: true } device)
        {
            await LiveSession.Close(ws, (WebSocketCloseStatus)4401, "unauthorized");
            return;
        }
        devices.Touch(device, options.Time.GetUtcNow());

        var session = new LiveSession(ws, device, host, Actions);
        _sessions[session] = 0;
        try
        {
            await session.Send("hello", new JsonObject
            {
                ["protocol"] = 1, ["server"] = options.ServerVersion, ["epoch"] = host.Snapshot?.Epoch,
                ["device"] = new JsonObject { ["id"] = device.Id, ["name"] = device.Name },
            });
            await session.RunAsync(ctx.RequestAborted);
        }
        finally { _sessions.TryRemove(session, out _); }
    }

    internal sealed record ClientFrame(string T, string? Id, JsonObject? Body);

    internal static ClientFrame? Frame(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject o && o["t"]?.GetValueKind() == JsonValueKind.String
                ? new ClientFrame(o["t"]!.GetValue<string>(), o["id"]?.GetValueKind() == JsonValueKind.String ? o["id"]!.GetValue<string>() : null, o["body"] as JsonObject)
                : null;
        }
        catch (JsonException) { return null; }
    }

    // ---------- the phone app ----------

    static readonly Assembly Web = typeof(RemoteServer).Assembly;
    static readonly ConcurrentDictionary<string, (byte[] Bytes, string ETag)?> Files = new();

    static (byte[], string)? Resource(string path) => Files.GetOrAdd(path, p =>
    {
        // MSBuild names files in subfolders "web/icons\icon-192.png" on Windows.
        using var s = Web.GetManifestResourceStream("web/" + p) ?? Web.GetManifestResourceStream("web/" + p.Replace('/', '\\'));
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        return (bytes, "\"" + Convert.ToHexStringLower(SHA256.HashData(bytes))[..16] + "\"");
    });

    static IResult Static(HttpContext ctx)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) || ctx.Request.Path.StartsWithSegments("/api")) return Results.NotFound();
        var path = ctx.Request.Path.Value!.TrimStart('/');
        if (path is "" or "pair") path = "index.html"; // one shell; the app routes by location

        if (Resource(path) is not var (bytes, etag))
        {
            if (path != "index.html") return Results.NotFound();
            return Results.Content("<!doctype html><title>TaskDock</title><p>The TaskDock phone app wasn't built. Run <code>npm ci &amp;&amp; npm run build</code> in remote-web.</p>", "text/html");
        }

        // Hashed assets (esbuild's app.YYCU2ZWZ.js) never change; the shell and the service worker must always be revalidated.
        var hashed = System.Text.RegularExpressions.Regex.IsMatch(path, @"\.[A-Za-z0-9]{8,}\.(js|css)$");
        ctx.Response.Headers.CacheControl = hashed ? "public, max-age=31536000, immutable" : "no-cache";
        ctx.Response.Headers.ETag = etag;
        if (ctx.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);
        return Results.Bytes(bytes, Path.GetExtension(path) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".webmanifest" => "application/manifest+json",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        });
    }
}
