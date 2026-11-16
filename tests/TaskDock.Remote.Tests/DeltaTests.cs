using System.Text.Json;
using System.Text.Json.Nodes;
using TaskDock.Core.Dock;
using TaskDock.Core.Tiles;

namespace TaskDock.Remote.Tests;

/// <summary>The phone's side of the protocol, in C#: the same rules remote-web/src/live.ts implements.</summary>
static class PhoneState
{
    /// <summary>Applies a delta to a snapshot body. Null when the phone must resync (new epoch or a gap in rev).</summary>
    public static JsonObject? Apply(JsonObject snap, JsonObject delta)
    {
        if (delta["epoch"]!.GetValue<string>() != snap["epoch"]!.GetValue<string>()) return null;
        if (delta["rev"]!.GetValue<long>() != snap["rev"]!.GetValue<long>() + 1) return null;

        var s = (JsonObject)snap.DeepClone();
        var byKey = new Dictionary<string, JsonNode>();
        foreach (var list in new[] { "tiles", "detected" })
            foreach (var t in s[list]!.AsArray()) byKey[t!["key"]!.GetValue<string>()] = t.DeepClone();
        var order = s["tiles"]!.AsArray().Select(t => t!["key"]!.GetValue<string>()).ToList();
        var detected = s["detected"]!.AsArray().Select(t => t!["key"]!.GetValue<string>()).ToList();

        foreach (var t in delta["upsert"]?.AsArray() ?? []) byKey[t!["key"]!.GetValue<string>()] = t.DeepClone();
        foreach (var k in delta["remove"]?.AsArray() ?? []) byKey.Remove(k!.GetValue<string>());
        if (delta["order"] is JsonArray o) order = o.Select(k => k!.GetValue<string>()).ToList();
        if (delta["detectedOrder"] is JsonArray d) detected = d.Select(k => k!.GetValue<string>()).ToList();

        s["tiles"] = new JsonArray(order.Select(k => byKey[k].DeepClone()).ToArray());
        s["detected"] = new JsonArray(detected.Select(k => byKey[k].DeepClone()).ToArray());
        foreach (var whole in new[] { "approvals", "media", "pill", "presets", "flags" })
            if (delta.ContainsKey(whole))
            {
                if (delta[whole] is null) s.Remove(whole); // the snapshot omits a missing player
                else s[whole] = delta[whole]!.DeepClone();
            }
        s["rev"] = delta["rev"]!.GetValue<long>();
        return s;
    }
}

public class DeltaTests
{
    static JsonObject Sample(string file) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "remote-samples", file)))!;

    [Fact]
    public void Golden_delta_applies_to_the_golden_snapshot()
    {
        var after = PhoneState.Apply(Sample("snapshot.json"), Sample("delta.json"));
        Assert.True(JsonNode.DeepEquals(Sample("snapshot-after-delta.json"), after), after?.ToJsonString());
    }

    [Fact]
    public void A_gap_or_a_new_epoch_means_resync()
    {
        var delta = Sample("delta.json");
        delta["rev"] = 9L;
        Assert.Null(PhoneState.Apply(Sample("snapshot.json"), delta));
        delta["rev"] = 8L;
        delta["epoch"] = "e2";
        Assert.Null(PhoneState.Apply(Sample("snapshot.json"), delta));
    }

    static readonly string[] Keys = [.. Enumerable.Range(0, 10).Select(i => $"unity|{i}"), "claude|a", "claude|b", "downloads|x"];
    static readonly string[] States = ["Idle", "Playing", "Compiling", "Done", "Failed"];

    static DockSnapshot RandomSnapshot(Random r, long rev)
    {
        var keys = Keys.OrderBy(_ => r.Next()).Take(r.Next(0, Keys.Length)).ToList();
        var split = r.Next(0, keys.Count + 1);
        TileView V(string k) => Snapshots.Tile(k, States[r.Next(States.Length)], (StatusClass)r.Next(6), r.Next(3) == 0 ? "renamed" : null)
            with { Progress = r.Next(3) == 0 ? r.NextDouble() : null, Detail = r.Next(2) == 0 ? "detail " + r.Next(3) : null };
        var approvals = Enumerable.Range(0, r.Next(0, 3)).Select(i => new ApprovalView($"ap{r.Next(4)}", "claude|a", "Bash", "api", "ls", "{}",
            Core.Claude.Risk.Risky, r.Next(2) == 0, 1, 2)).ToList();
        var media = r.Next(3) == 0 ? null : new MediaView("Spotify.exe", "Spotify", "Song " + r.Next(4), "Band", r.Next(2) == 0, true, true, r.Next(2) == 0,
            r.Next(200), 200, 1, null, [new("Spotify.exe", "Spotify", true)], r.Next(2) == 0);
        return new DockSnapshot("e", rev, new PillView("p" + r.Next(3), (StatusClass)r.Next(6)), keys.Take(split).Select(V).ToList(),
            keys.Skip(split).Select(V).ToList(), approvals, media, new PresetsView("Default", r.Next(2) == 0 ? ["Default"] : ["Default", "Work"]),
            new FlagsView(r.Next(2) == 0, r.Next(2) == 0));
    }

    static JsonObject Full(DockSnapshot s, long rev) => (JsonObject)JsonSerializer.SerializeToNode(s with { Rev = rev }, DockJson.Options)!;

    [Fact]
    public void A_thousand_random_edit_sequences_rebuild_the_full_snapshot()
    {
        var r = new Random(20261116);
        for (var run = 0; run < 1000; run++)
        {
            var enc = new DeltaEncoder();
            var phone = (JsonObject)enc.Snapshot(RandomSnapshot(r, 1));
            for (var step = 0; step < 8; step++)
            {
                var next = RandomSnapshot(r, 0);
                var delta = enc.Delta(next);
                var want = Full(next, phone["rev"]!.GetValue<long>() + (delta == null ? 0 : 1));
                if (delta == null)
                {
                    Assert.True(JsonNode.DeepEquals(want, phone), $"run {run} step {step}: no delta but the phone is behind");
                    continue;
                }
                phone = PhoneState.Apply(phone, delta) ?? throw new Xunit.Sdk.XunitException($"run {run} step {step}: unexpected resync");
                Assert.True(JsonNode.DeepEquals(want, phone), $"run {run} step {step}\nwant {want}\ngot  {phone}\ndelta {delta}");
            }
        }
    }

    [Fact]
    public void Same_content_sends_nothing()
    {
        var enc = new DeltaEncoder();
        var s = Snapshots.Base();
        enc.Snapshot(s);
        Assert.Null(enc.Delta(s with { Rev = 42 }));
    }

    [Theory]
    [InlineData("tailscale-running.json", "Running", "pc.tail1234.ts.net", true, true, "me@example.com", true)]
    [InlineData("tailscale-loggedout.json", "NeedsLogin", null, false, false, null, false)]
    [InlineData("tailscale-https-off.json", "Running", "pc.tail1234.ts.net", true, false, "me@example.com", false)]
    [InlineData("tailscale-magicdns-off.json", "Running", "pc.tail1234.ts.net", false, true, "me@example.com", false)]
    public void Tailscale_status_fixtures(string file, string state, string? dns, bool magic, bool certs, string? login, bool ready)
    {
        var s = TailscaleStatus.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", file)))!;
        Assert.Equal(new TailscaleStatus(state, dns, magic, certs, login), s);
        Assert.Equal(ready, s.Ready);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Garbage_status_is_not_ready(string text) => Assert.False(TailscaleStatus.Parse(text)?.Ready ?? false);
}

public class ServeTests
{
    static string Fixture(string f) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", f));

    [Theory]
    [InlineData("serve-empty.json", 443)]
    [InlineData("serve-taskdock.json", 443)] // our own mapping from an earlier run: reuse 443
    [InlineData("serve-other.json", 8443)]   // somebody else's site: don't take it over
    public void Https_port_avoids_someone_elses_site(string file, int want) =>
        Assert.Equal(want, TailscaleStatus.PickHttpsPort(Fixture(file), "pc.tail1234.ts.net"));

    [Fact]
    public async Task On_8443_the_host_and_origin_carry_the_port()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taskdock-remote-" + Guid.NewGuid().ToString("N"));
        var host = new FakeHost();
        host.Publish(Snapshots.Base());
        await using var server = new RemoteServer(new RemoteOptions { BasePort = 0, HostName = Harness.Name, HttpsPort = 8443, DataDir = dir }, host, new DeviceStore(dir));
        await server.StartAsync();
        Assert.StartsWith($"https://{Harness.Name}:8443/pair#c=", server.PairingUrl("x"));

        using var c = new HttpClient();
        async Task<System.Net.HttpStatusCode> Get(string hostHeader, string? origin = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/");
            req.Headers.Host = hostHeader;
            req.Headers.Add("Tailscale-User-Login", Harness.Owner);
            if (origin != null) req.Headers.Add("Origin", origin);
            return (await c.SendAsync(req)).StatusCode;
        }
        Assert.Equal(System.Net.HttpStatusCode.OK, await Get($"{Harness.Name}:8443", $"https://{Harness.Name}:8443"));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, await Get(Harness.Name));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, await Get($"{Harness.Name}:8443", $"https://{Harness.Name}"));
    }
}
