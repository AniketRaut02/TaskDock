using System.Diagnostics;
using System.IO;
using TaskDock.Remote;

namespace TaskDock.App;

/// <summary>Runs tailscale.exe as the current user (S3: no elevation needed). Never installs or signs in for the user.</summary>
static class TailscaleCli
{
    public static string? Exe()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var p = Path.Combine(dir.Trim('"'), "tailscale.exe"); if (File.Exists(p)) return p; }
            catch (ArgumentException) { } // a malformed PATH entry
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>Null when Tailscale isn't installed or didn't answer.</summary>
    public static async Task<TailscaleStatus?> StatusAsync() =>
        await Run("status", "--json") is (0, var json) ? TailscaleStatus.Parse(json) : null;

    /// <summary>The HTTPS port to use: 443 unless another site already has it.</summary>
    public static async Task<int> PickHttpsPortAsync(string host) =>
        await Run("serve", "status", "--json") is (0, var json) ? TailscaleStatus.PickHttpsPort(json, host) : 443;

    public static async Task<string?> ServeAsync(int httpsPort, int localPort) =>
        await Run("serve", "--bg", $"--https={httpsPort}", $"http://127.0.0.1:{localPort}") is (0, _) ? null : "tailscale serve failed";

    public static Task ServeOffAsync(int httpsPort) => Run("serve", $"--https={httpsPort}", "off");

    static async Task<(int Code, string Out)> Run(params string[] args)
    {
        if (Exe() is not { } exe) return (-1, "");
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await p.WaitForExitAsync(timeout.Token);
            var err = await stderr;
            if (p.ExitCode != 0) Serilog.Log.Warning("tailscale {Verb} exited {Code}: {Err}", args[0], p.ExitCode, err.Trim()); // no secrets in Tailscale's errors
            return (p.ExitCode, await stdout);
        }
        catch (OperationCanceledException) { Serilog.Log.Warning("tailscale {Verb} timed out", args[0]); return (-1, ""); }
        catch (System.ComponentModel.Win32Exception ex) { Serilog.Log.Warning(ex, "Could not run tailscale"); return (-1, ""); }
    }
}
