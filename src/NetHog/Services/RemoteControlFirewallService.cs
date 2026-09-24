using System.Diagnostics;

namespace NetHog.Services;

internal sealed class RemoteControlFirewallService
{
    private const string RuleName = "NetHog - LAN control coordination";

    public Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;

        var command =
            $"if (-not (Get-NetFirewallRule -DisplayName '{Escape(RuleName)}' -ErrorAction SilentlyContinue)) " +
            $"{{ New-NetFirewallRule -DisplayName '{Escape(RuleName)}' -Direction Inbound -Action Allow " +
            $"-Profile Any -Protocol UDP -LocalPort {RemoteControlCoordinator.CoordinationPort} " +
            "-RemoteAddress LocalSubnet | Out-Null }}";
        return RunPowerShellAsync(command, cancellationToken);
    }

    public Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        return RunPowerShellAsync(
            $"Remove-NetFirewallRule -DisplayName '{Escape(RuleName)}' -ErrorAction SilentlyContinue",
            cancellationToken);
    }

    private static async Task RunPowerShellAsync(string command, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Windows could not start the LAN coordination firewall command.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException(error.Trim());
        }
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
