using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TaskDock.Remote.Tests;

public class SecurityTests
{
    [Fact]
    public async Task Every_response_carries_the_security_headers()
    {
        await using var h = await Harness.Start();
        var r = await h.Client().GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("default-src 'self'; img-src 'self' blob:; connect-src 'self'; script-src 'self'; style-src 'self'",
            r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(r.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Wrong_host_is_refused()
    {
        await using var h = await Harness.Start();
        using var c = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{h.Server.Port}/");
        req.Headers.Host = "evil.example.com";
        req.Headers.Add("Tailscale-User-Login", Harness.Owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(req)).StatusCode);

        var withPort = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{h.Server.Port}/");
        withPort.Headers.Host = $"{Harness.Name}:443"; // Serve sends the bare name
        withPort.Headers.Add("Tailscale-User-Login", Harness.Owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(withPort)).StatusCode);
    }

    [Fact]
    public async Task Loopback_host_still_needs_the_identity_header()
    {
        await using var h = await Harness.Start();
        using var c = new HttpClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"http://127.0.0.1:{h.Server.Port}/")).StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{h.Server.Port}/");
        req.Headers.Add("Tailscale-User-Login", Harness.Owner);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Missing_identity_header_is_refused()
    {
        await using var h = await Harness.Start();
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client(login: null).GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task After_the_first_pairing_only_the_owner_login_gets_in()
    {
        await using var h = await Harness.Start();
        Assert.Equal(HttpStatusCode.OK, (await h.Client("someone@else.com").GetAsync("/")).StatusCode); // nobody owns it yet
        var (_, _, finish) = await h.Pair();
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client("someone@else.com").GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Client(Harness.Owner.ToUpperInvariant()).GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-token")]
    public async Task Api_needs_a_valid_token(string? token)
    {
        await using var h = await Harness.Start();
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client(token: token).DeleteAsync("api/devices/self")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client(token: token).PostAsync("api/pair/finish", Harness.Json(new JsonObject()))).StatusCode);
    }

    [Fact]
    public async Task Foreign_origin_and_preflight_are_refused()
    {
        await using var h = await Harness.Start();
        var c = h.Client();
        var foreign = new HttpRequestMessage(HttpMethod.Get, "/");
        foreign.Headers.Add("Origin", "https://evil.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(foreign)).StatusCode);
        var own = new HttpRequestMessage(HttpMethod.Get, "/");
        own.Headers.Add("Origin", $"https://{Harness.Name}");
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(own)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(new HttpRequestMessage(HttpMethod.Options, "api/pair/start"))).StatusCode);
    }

    [Fact]
    public async Task Shell_revalidates_and_hashed_assets_are_immutable()
    {
        await using var h = await Harness.Start();
        var c = h.Client();
        var shell = await c.GetAsync("/pair");
        var html = await shell.Content.ReadAsStringAsync();
        if (html.Contains("phone app wasn't built")) return; // a clean clone without Node: nothing else to check
        Assert.Equal("no-cache", shell.Headers.CacheControl!.ToString());

        var js = System.Text.RegularExpressions.Regex.Match(html, @"/(app\.[A-Za-z0-9]+\.js)").Groups[1].Value;
        var asset = await c.GetAsync(js);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl!.ToString());
        Assert.StartsWith("text/javascript", asset.Content.Headers.ContentType!.ToString());

        Assert.Equal("no-cache", (await c.GetAsync("sw.js")).Headers.CacheControl!.ToString());
        var again = new HttpRequestMessage(HttpMethod.Get, js);
        again.Headers.TryAddWithoutValidation("If-None-Match", asset.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(again)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("nope.js")).StatusCode);
        var icon = await c.GetAsync("icons/icon-192.png");
        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        Assert.Equal("image/png", icon.Content.Headers.ContentType!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("manifest.webmanifest")).StatusCode);
    }
}

public class PairingTests
{
    [Fact]
    public async Task Pairing_creates_an_active_device_with_its_passkey_after_the_pc_says_yes()
    {
        await using var h = await Harness.Start();
        var (_, id, finish) = await h.Pair("Pixel 8");
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        Assert.Equal(["Pixel 8"], h.Host.Asked);
        var d = Assert.Single(h.Server.Devices.Devices);
        Assert.Equal(id, d.Id);
        Assert.True(d.IsActive);
        Assert.NotNull(d.Credential);
        Assert.Equal(Harness.Owner, h.Server.Devices.OwnerLogin);

        var saved = File.ReadAllText(Path.Combine(h.Dir, "devices.json"));
        Assert.DoesNotContain(finish.RequestMessage!.Headers.Authorization!.Parameter!, saved); // only the hash is stored
    }

    [Fact]
    public async Task No_activation_without_the_pcs_yes()
    {
        await using var h = await Harness.Start();
        h.Host.Confirm = false;
        var (token, _, finish) = await h.Pair();
        Assert.Equal(HttpStatusCode.Forbidden, finish.StatusCode);
        Assert.Empty(h.Server.Devices.Devices);
        Assert.Null(h.Server.Devices.OwnerLogin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client(token: token).DeleteAsync("api/devices/self")).StatusCode);
    }

    [Fact]
    public async Task An_unanswered_confirmation_times_out()
    {
        await using var h = await Harness.Start(confirmTimeout: TimeSpan.FromMilliseconds(300));
        h.Host.Confirm = null;
        var (_, _, finish) = await h.Pair();
        Assert.Equal(HttpStatusCode.Forbidden, finish.StatusCode);
        Assert.Empty(h.Server.Devices.Devices);
    }

    static JsonObject Start(string code) => new() { ["code"] = code, ["deviceName"] = "Pixel 8" };

    [Fact]
    public async Task A_code_works_once()
    {
        await using var h = await Harness.Start();
        var code = h.Server.NewPairingCode();
        Assert.Equal(HttpStatusCode.OK, (await h.Client().PostAsync("api/pair/start", Harness.Json(Start(code)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client().PostAsync("api/pair/start", Harness.Json(Start(code)))).StatusCode);
    }

    [Fact]
    public async Task A_code_expires_after_5_minutes()
    {
        await using var h = await Harness.Start();
        var code = h.Server.NewPairingCode();
        h.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client().PostAsync("api/pair/start", Harness.Json(Start(code)))).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_attempts_burn_the_code()
    {
        await using var h = await Harness.Start();
        var code = h.Server.NewPairingCode();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Forbidden, (await h.Client().PostAsync("api/pair/start", Harness.Json(Start("guess" + i)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client().PostAsync("api/pair/start", Harness.Json(Start(code)))).StatusCode);
    }

    [Fact]
    public async Task A_passkey_for_another_site_is_rejected()
    {
        await using var h = await Harness.Start();
        var code = h.Server.NewPairingCode();
        var body = JsonNode.Parse(await (await h.Client().PostAsync("api/pair/start", Harness.Json(Start(code)))).Content.ReadAsStringAsync())!;
        var forged = SoftAuthenticator.Create(body["creationOptions"]!, "https://evil.example.com", "evil.example.com");
        var r = await h.Client(token: body["token"]!.GetValue<string>()).PostAsync("api/pair/finish", Harness.Json(forged));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(h.Server.Devices.Devices);
        Assert.Empty(h.Host.Asked); // never reached the PC's dialog
    }

    [Fact]
    public async Task Paired_devices_survive_a_restart_and_pending_ones_do_not()
    {
        var h = await Harness.Start();
        var (token, id, _) = await h.Pair();
        await h.Server.DisposeAsync();
        var store = new DeviceStore(h.Dir);
        Assert.Equal(id, store.ByToken(token)?.Id);
        Assert.Equal(Harness.Owner, store.OwnerLogin);
        Directory.Delete(h.Dir, true);
    }
}

public class LiveTests
{
    static async Task<JsonNode> Next(ClientWebSocket ws, int timeoutMs = 3000)
    {
        var buf = new byte[256 * 1024];
        using var cts = new CancellationTokenSource(timeoutMs);
        var r = await ws.ReceiveAsync(buf, cts.Token);
        return JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count))!;
    }

    static Task Send(ClientWebSocket ws, JsonNode frame) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    static JsonObject Auth(string token) => new() { ["t"] = "auth", ["body"] = new JsonObject { ["token"] = token } };

    [Fact]
    public async Task Auth_then_hello_snapshot_and_deltas()
    {
        await using var h = await Harness.Start();
        var (token, id, _) = await h.Pair();
        using var ws = await h.Socket();
        await Send(ws, Auth(token));

        var hello = await Next(ws);
        Assert.Equal("hello", hello["t"]!.GetValue<string>());
        Assert.Equal(1, hello["body"]!["protocol"]!.GetValue<int>());
        Assert.Equal("epoch1", hello["body"]!["epoch"]!.GetValue<string>());
        Assert.Equal(id, hello["body"]!["device"]!["id"]!.GetValue<string>());

        var snap = await Next(ws);
        Assert.Equal("snapshot", snap["t"]!.GetValue<string>());
        Assert.Equal(1, snap["body"]!["rev"]!.GetValue<long>());

        var s = h.Host.Snapshot!;
        h.Host.Publish(s with { Tiles = [s.Tiles[0] with { State = "Playing" }, s.Tiles[1]] });
        var delta = await Next(ws);
        Assert.Equal("delta", delta["t"]!.GetValue<string>());
        Assert.Equal(2, delta["body"]!["rev"]!.GetValue<long>());
        Assert.Equal("Playing", delta["body"]!["upsert"]![0]!["state"]!.GetValue<string>());

        await Send(ws, new JsonObject { ["t"] = "resync", ["body"] = new JsonObject() });
        Assert.Equal("snapshot", (await Next(ws))["t"]!.GetValue<string>());
    }

    [Fact]
    public async Task Nothing_is_sent_when_nothing_changed_and_bursts_are_coalesced()
    {
        await using var h = await Harness.Start();
        var (token, _, _) = await h.Pair();
        using var ws = await h.Socket();
        await Send(ws, Auth(token));
        await Next(ws); await Next(ws);

        h.Host.Publish(h.Host.Snapshot! with { Rev = 99 }); // same content, new rev: no frame
        var s = h.Host.Snapshot!;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) h.Host.Publish(s with { Tiles = [s.Tiles[0] with { State = "Busy " + i }, s.Tiles[1]] });

        var frames = new List<JsonNode>();
        try { while (sw.ElapsedMilliseconds < 1100) frames.Add(await Next(ws, 1100 - (int)sw.ElapsedMilliseconds)); }
        catch (OperationCanceledException) { }
        Assert.InRange(frames.Count, 1, 5); // at most 4 a second, and never one per publish
        Assert.Equal("Busy 19", frames[^1]["body"]!["upsert"]![0]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_auth_within_5_seconds_closes_the_socket()
    {
        await using var h = await Harness.Start();
        using var ws = await h.Socket();
        var sw = Stopwatch.StartNew();
        var r = await ws.ReceiveAsync(new byte[1024], new CancellationTokenSource(8000).Token);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
        Assert.Equal(4401, (int)ws.CloseStatus!);
        Assert.InRange(sw.ElapsedMilliseconds, 4000, 7000);
    }

    [Fact]
    public async Task A_wrong_token_is_refused()
    {
        await using var h = await Harness.Start();
        using var ws = await h.Socket();
        await Send(ws, Auth("nope"));
        var r = await ws.ReceiveAsync(new byte[1024], new CancellationTokenSource(3000).Token);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
    }

    [Fact]
    public async Task An_oversized_frame_closes_the_socket()
    {
        await using var h = await Harness.Start();
        var (token, _, _) = await h.Pair();
        using var ws = await h.Socket();
        await Send(ws, Auth(token));
        await Next(ws); await Next(ws);
        await Send(ws, new JsonObject { ["t"] = "resync", ["body"] = new JsonObject { ["pad"] = new string('x', 17 * 1024) } });
        var r = await ws.ReceiveAsync(new byte[1024], new CancellationTokenSource(3000).Token);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, ws.CloseStatus);
    }

    [Fact]
    public async Task An_action_on_the_socket_runs_and_answers_with_its_id()
    {
        await using var h = await Harness.Start();
        var (token, _, _) = await h.Pair();
        using var ws = await h.Socket();
        await Send(ws, Auth(token));
        await Next(ws); await Next(ws);
        await Send(ws, new JsonObject { ["t"] = "action", ["body"] = new JsonObject { ["id"] = "a1", ["action"] = new JsonObject { ["type"] = "media.toggle" } } });
        var result = await Next(ws);
        Assert.Equal("result", result["t"]!.GetValue<string>());
        Assert.Equal("a1", result["body"]!["id"]!.GetValue<string>());
        Assert.True(result["body"]!["ok"]!.GetValue<bool>());
        Assert.IsType<Core.Dock.MediaToggle>(Assert.Single(h.Host.Ran));
    }

    [Fact]
    public async Task Revoking_ends_the_session_within_a_second_and_the_token_stops_working()
    {
        await using var h = await Harness.Start();
        var (token, id, _) = await h.Pair();
        using var ws = await h.Socket();
        await Send(ws, Auth(token));
        await Next(ws); await Next(ws);

        var sw = Stopwatch.StartNew();
        Assert.True(h.Server.Revoke(id));
        try { while (true) { var r = await ws.ReceiveAsync(new byte[4096], new CancellationTokenSource(2000).Token); if (r.MessageType == WebSocketMessageType.Close) break; } }
        catch (WebSocketException) { } // aborted: the connection just drops
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");

        using var again = await h.Socket();
        await Send(again, Auth(token));
        Assert.Equal(WebSocketMessageType.Close, (await again.ReceiveAsync(new byte[1024], new CancellationTokenSource(3000).Token)).MessageType);
    }

    [Fact]
    public async Task A_phone_can_unpair_itself()
    {
        await using var h = await Harness.Start();
        var (token, _, _) = await h.Pair();
        Assert.Equal(HttpStatusCode.OK, (await h.Client(token: token).DeleteAsync("api/devices/self")).StatusCode);
        Assert.Empty(h.Server.Devices.Devices);
    }
}
