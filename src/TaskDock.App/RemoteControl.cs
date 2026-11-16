using System.IO;
using Serilog;
using TaskDock.Remote;

namespace TaskDock.App;

/// <summary>Turns TaskDock Remote on and off: checks Tailscale, starts RemoteServer, points `tailscale serve` at it.</summary>
sealed class RemoteControl(DockHost dock)
{
    int _httpsPort = 443;

    public RemoteServer? Server { get; private set; }
    /// <summary>The address phones use, while on.</summary>
    public string? Address { get; private set; }

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
        return null;
    }

    public async Task DisableAsync()
    {
        if (Server is not { } server) return;
        Server = null;
        Address = null;
        await TailscaleCli.ServeOffAsync(_httpsPort);
        await server.DisposeAsync();
        Log.Information("Remote off");
    }

    /// <summary>On exit the serve mapping stays (it simply has nothing to reach); the next start re-points it.</summary>
    public async Task StopAsync()
    {
        if (Server is { } s) await s.DisposeAsync();
    }
}
