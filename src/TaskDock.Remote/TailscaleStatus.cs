using System.Text.Json;

namespace TaskDock.Remote;

/// <summary>
/// The parts of `tailscale status --json` setup needs. Field names recorded in spike S3 (Tailscale 1.104, Windows):
/// BackendState, Self.DNSName (trailing dot), CertDomains, CurrentTailnet.MagicDNSEnabled, User[Self.UserID].LoginName.
/// </summary>
public sealed record TailscaleStatus(string BackendState, string? DnsName, bool MagicDns, bool HttpsCerts, string? Login)
{
    public bool Running => BackendState == "Running";
    /// <summary>Ready to serve the phone over HTTPS on the tailnet name.</summary>
    public bool Ready => Running && MagicDns && HttpsCerts && DnsName != null;

    public static TailscaleStatus? Parse(string json)
    {
        try
        {
            var r = JsonDocument.Parse(json).RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string? Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var self = r.TryGetProperty("Self", out var s) ? s : default;
            var dns = Str(self, "DNSName")?.TrimEnd('.');
            var certs = r.TryGetProperty("CertDomains", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(x => x.GetString()).ToList() : [];
            var magic = r.TryGetProperty("CurrentTailnet", out var t) && t.ValueKind == JsonValueKind.Object
                && t.TryGetProperty("MagicDNSEnabled", out var m) && m.ValueKind == JsonValueKind.True;

            string? login = null;
            if (self.ValueKind == JsonValueKind.Object && self.TryGetProperty("UserID", out var uid) && r.TryGetProperty("User", out var users)
                && users.ValueKind == JsonValueKind.Object && users.TryGetProperty(uid.ToString(), out var u))
                login = Str(u, "LoginName");

            return new(Str(r, "BackendState") ?? "", string.IsNullOrEmpty(dns) ? null : dns, magic,
                dns != null && certs.Contains(dns, StringComparer.OrdinalIgnoreCase), login);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Where `tailscale serve status --json` sends https://host:port/, or null when nothing is served there.
    /// Shape recorded in S3: Web["host:443"].Handlers["/"].Proxy.
    /// </summary>
    public static string? ServeTarget(string serveStatusJson, string host, int httpsPort)
    {
        try
        {
            var r = JsonDocument.Parse(serveStatusJson).RootElement;
            return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("Web", out var web) && web.ValueKind == JsonValueKind.Object
                && web.TryGetProperty($"{host}:{httpsPort}", out var site) && site.TryGetProperty("Handlers", out var hs)
                && hs.ValueKind == JsonValueKind.Object && hs.TryGetProperty("/", out var root) && root.TryGetProperty("Proxy", out var p)
                ? p.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>443 unless something other than TaskDock already uses it there; then 8443.</summary>
    public static int PickHttpsPort(string serveStatusJson, string host) =>
        ServeTarget(serveStatusJson, host, 443) is { } t && !IsTaskDock(t) ? 8443 : 443;

    static bool IsTaskDock(string proxy) =>
        Uri.TryCreate(proxy, UriKind.Absolute, out var u) && u.Host == "127.0.0.1" && u.Port is >= 47830 and <= 47839;
}
