using System.IO;
using Serilog;
using TaskDock.Core.Dock;
using TaskDock.Core.Tiles;
using TaskDock.Hub;
using TaskDock.Platform;
using TaskDock.Remote;

namespace TaskDock.App;

/// <summary>
/// Turns TaskDock Remote on and off (checks Tailscale, starts RemoteServer, points `tailscale serve` at it), and runs
/// Away mode: who is at the PC, how long approvals wait, pushes to locked phones, and keeping the PC awake.
/// </summary>
sealed class RemoteControl(DockHost dock, Action holdChanged)
{
    public static readonly TimeSpan AwayAfter = TimeSpan.FromSeconds(180);
    public const int RemoteHoldSeconds = 600;

    int _httpsPort = 443;
    DateTimeOffset _presentSince = DateTimeOffset.UtcNow;

    public RemoteServer? Server { get; private set; }
    /// <summary>The address phones use, while on.</summary>
    public string? Address { get; private set; }
    /// <summary>Remote on, a phone paired, and no keyboard or mouse input for 3 minutes. Phone actions never count.</summary>
    public bool Away { get; private set; }
    public bool KeepingAwake { get; private set; }

    /// <summary>Null when it's on; otherwise what the user has to do.</summary>
    public async Task<string?> EnableAsync()
    {
        if (Server != null) return null;
        if (TailscaleCli.Exe() == null) return "Tailscale isn't installed on this PC. Install it from tailscale.com/download/windows and sign in.";
        if (await TailscaleCli.StatusAsync() is not { } s) return "Tailscale didn't answer. Is it running?";
        if (!s.Running) return "Open Tailscale and sign in, then try again.";
        if (!s.MagicDns || !s.HttpsCerts || s.DnsName is not { } name)
            return "Turn on MagicDNS and HTTPS Certificates in the Tailscale admin console (login.tailscale.com/admin/dns), then try again.";

        _httpsPort = await TailscaleCli.PickHttpsPortAsync(name);
        var server = new RemoteServer(new RemoteOptions { HostName = name, HttpsPort = _httpsPort, Logging = l => l.AddSerilog(Log.Logger) },
            dock, new DeviceStore(Path.Combine(Paths.Local, "remote")));
        try { await server.StartAsync(); }
        catch (IOException ex) { Log.Warning(ex, "Remote server could not start"); return "No free port for the phone server (47830–47839)."; }

        if (await TailscaleCli.ServeAsync(_httpsPort, server.Port) is { } err)
        {
            await server.DisposeAsync();
            return $"Couldn't share TaskDock on your tailnet ({err}).";
        }
        Server = server;
        Address = _httpsPort == 443 ? $"https://{name}" : $"https://{name}:{_httpsPort}";
        Log.Information("Remote on: 127.0.0.1:{Port} served on HTTPS port {Https}", server.Port, _httpsPort);
        holdChanged();
        return null;
    }

    public async Task DisableAsync()
    {
        if (Server is not { } server) return;
        Server = null;
        Address = null;
        Tick(null, null); // leave Away and keep-awake
        await TailscaleCli.ServeOffAsync(_httpsPort);
        await server.DisposeAsync();
        Log.Information("Remote off");
        holdChanged();
    }

    /// <summary>On exit the serve mapping stays (it simply has nothing to reach); the next start re-points it.</summary>
    public async Task StopAsync()
    {
        if (KeepingAwake) Win32.KeepSystemAwake(false);
        if (Server is { } s) await s.DisposeAsync();
    }

    /// <summary>When a held approval goes back to the terminal, given who is at the PC.</summary>
    public DateTimeOffset Deadline(PendingApproval p, TimeSpan atPc) => HoldRule.Deadline(p.HeldAt, p.Expires, Away, _presentSince, atPc);

    /// <summary>Once a second on the UI thread. Null broker/registry: just leave Away and keep-awake.</summary>
    public void Tick(ApprovalBroker? broker, TileRegistry? registry, TimeSpan atPc = default)
    {
        var now = DateTimeOffset.UtcNow;
        var away = Server is { } s && broker != null && s.Devices.Devices.Any(d => d.IsActive) && Win32.IdleTime() >= AwayAfter;
        if (away != Away)
        {
            Away = away;
            if (!away) _presentSince = now; // coming back starts a fresh wait; it never makes an old approval vanish at once
            Log.Information("Presence: {State}", away ? "Away" : "Present");
        }

        if (!away && broker != null)
            foreach (var p in broker.Pending)
                if (now >= HoldRule.Deadline(p.HeldAt, p.Expires, false, _presentSince, atPc))
                    broker.Resolve(p.Id, ApprovalChoice.Terminal);

        var keep = away && (registry!.Tiles.Any(t => t.Class == StatusClass.Busy) || broker!.Pending.Count > 0 || Server!.AnyPhoneOpen);
        if (keep != KeepingAwake)
        {
            KeepingAwake = keep;
            Win32.KeepSystemAwake(keep);
            Log.Information("Keep awake: {On}", keep);
        }
    }

    /// <summary>A push to locked phones, if the push policy says so (only while Away).</summary>
    public void Notify(PushMessageFor m, bool quietHours) => Server?.Notify(m, Away, quietHours);
}
