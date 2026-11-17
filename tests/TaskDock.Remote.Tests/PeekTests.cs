using System.Net;
using System.Text.Json.Nodes;

namespace TaskDock.Remote.Tests;

/// <summary>Phase 5: Peek needs a fingerprint once per 5 minutes, scoped so it can't approve anything (and back).</summary>
public class PeekTests
{
    static async Task<(Harness H, HttpClient C)> Paired()
    {
        var h = await Harness.Start();
        var (token, _, _) = await h.Pair();
        return (h, h.Client(token: token));
    }

    static async Task<JsonNode> Options(HttpClient c, JsonObject body)
    {
        var r = await c.PostAsync("api/stepup/options", Harness.Json(body));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
    }

    static Task<JsonNode> PeekOptions(HttpClient c) => Options(c, new JsonObject { ["purpose"] = "peek" });

    static JsonObject Sign(Harness h, JsonNode options) => h.Phone.Assert(options, $"https://{Harness.Name}", Harness.Name);

    static Task<HttpResponseMessage> Grant(HttpClient c, JsonObject assertion) => c.PostAsync("api/peek/grant", Harness.Json(new JsonObject { ["assertion"] = assertion }));

    [Fact]
    public async Task Without_a_grant_peek_asks_for_the_fingerprint()
    {
        var (h, c) = await Paired();
        await using var _ = h;
        var r = await c.GetAsync("api/peek/unity%7Ca");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("stepup_required", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_fingerprint_grants_5_minutes_of_uncached_jpegs()
    {
        var (h, c) = await Paired();
        await using var _ = h;
        Assert.Equal(HttpStatusCode.OK, (await Grant(c, Sign(h, await PeekOptions(c)))).StatusCode);

        var r = await c.GetAsync("api/peek/unity%7Ca");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/jpeg", r.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-store", r.Headers.CacheControl!.ToString());
        Assert.Equal(FakeHost.Jpeg, await r.Content.ReadAsByteArrayAsync());

        h.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("api/peek/unity%7Ca")).StatusCode);
    }

    [Fact]
    public async Task At_most_one_capture_a_second()
    {
        var (h, c) = await Paired();
        await using var _ = h;
        await Grant(c, Sign(h, await PeekOptions(c)));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("api/peek/unity%7Ca")).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await c.GetAsync("api/peek/unity%7Ca")).StatusCode);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("api/peek/unity%7Ca")).StatusCode);
    }

    [Theory]
    [InlineData("unity%7Cmin", HttpStatusCode.Conflict, "minimized")]
    [InlineData("unity%7Cgone", HttpStatusCode.NotFound, "not_found")]
    public async Task Windows_that_cant_be_shown_say_why(string key, HttpStatusCode status, string code)
    {
        var (h, c) = await Paired();
        await using var _ = h;
        await Grant(c, Sign(h, await PeekOptions(c)));
        var r = await c.GetAsync($"api/peek/{key}");
        Assert.Equal(status, r.StatusCode);
        Assert.Contains(code, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_approval_fingerprint_cannot_grant_peek()
    {
        var (h, c) = await Paired();
        await using var _ = h;
        var forApproval = Sign(h, await Options(c, new JsonObject { ["purpose"] = "approval", ["approvalId"] = "bash1", ["choice"] = "allow" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await Grant(c, forApproval)).StatusCode);
    }

    [Fact]
    public async Task A_peek_fingerprint_cannot_approve()
    {
        var (h, c) = await Paired();
        await using var _ = h;
        var forPeek = Sign(h, await PeekOptions(c));
        var r = await c.PostAsync("api/action", Harness.Json(new JsonObject
        {
            ["id"] = "1", ["action"] = new JsonObject { ["type"] = "approval.allow", ["approvalId"] = "bash1", ["assertion"] = forPeek },
        }));
        Assert.Equal("stepup_failed", JsonNode.Parse(await r.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
        Assert.Empty(h.Host.Ran);
    }

    [Fact]
    public async Task Peeks_are_in_the_activity_log()
    {
        var (h, c) = await Paired();
        await using var _ = h;
        await Grant(c, Sign(h, await PeekOptions(c)));
        await c.GetAsync("api/peek/unity%7Ca");
        var log = JsonNode.Parse(await c.GetStringAsync("api/activity"))!.AsArray();
        Assert.Equal(("peek", "unity|a"), (log[0]!["type"]!.GetValue<string>(), log[0]!["target"]!.GetValue<string>()));
    }
}
