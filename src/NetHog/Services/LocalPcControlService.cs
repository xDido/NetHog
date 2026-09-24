using System.Diagnostics;
using NetHog.Models;

namespace NetHog.Services;

/// <summary>
/// Applies controls to the current Windows PC using built-in Windows policy cmdlets.
/// The existing ARP engine cannot intercept its own adapter, so local controls use
/// Windows QoS for outbound throttling and Windows Firewall for an outbound block.
/// </summary>
public sealed class LocalPcControlService
{
    private const string QosPolicyName = "NetHog - Current PC upload limit";
    private const string FirewallRuleName = "NetHog - Current PC internet block";

    public bool IsSupported => OperatingSystem.IsWindows();

    public async Task ApplyRuleAsync(DeviceControlRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!IsSupported) throw new PlatformNotSupportedException("Current-PC controls require Windows 10 or 11.");
        if (rule.DownloadLimitMbps is not null)
        {
            throw new InvalidOperationException(
                "Windows can throttle this PC's outbound traffic here, but it cannot shape inbound traffic with the built-in policy used by NetHog. Leave Download blank and set Upload instead.");
        }

        ValidateRule(rule);
        await RemoveRuleAsync(cancellationToken);
        try
        {
            if (rule.UploadLimitMbps is { } upload)
            {
                var bitsPerSecond = checked((long)upload * 1_000_000L);
                await RunPowerShellAsync(
                    $"New-NetQosPolicy -Name '{Escape(QosPolicyName)}' -AppPathNameMatchCondition '*' -NetworkProfile All -ThrottleRateActionBitsPerSecond {bitsPerSecond} -PolicyStore ActiveStore | Out-Null",
                    cancellationToken);
            }

            if (rule.BlockInternet)
            {
                await RunPowerShellAsync(
                    $"New-NetFirewallRule -DisplayName '{Escape(FirewallRuleName)}' -Direction Outbound -Action Block -Profile Any -RemoteAddress '0.0.0.0/0','::/0' -Protocol Any | Out-Null",
                    cancellationToken);
            }
        }
        catch
        {
            try { await RemoveRuleAsync(CancellationToken.None); }
            catch { /* preserve the original policy error */ }
            throw;
        }
    }

    public Task RemoveRuleAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return Task.CompletedTask;
        return RunPowerShellAsync(
            $"$qos = Get-NetQosPolicy -Name '{Escape(QosPolicyName)}' -ErrorAction SilentlyContinue; " +
            "if ($null -ne $qos) { Remove-NetQosPolicy -Name '" + Escape(QosPolicyName) + "' -Confirm:$false -ErrorAction Stop }; " +
            $"$firewall = Get-NetFirewallRule -DisplayName '{Escape(FirewallRuleName)}' -ErrorAction SilentlyContinue; " +
            "if ($null -ne $firewall) { Remove-NetFirewallRule -DisplayName '" + Escape(FirewallRuleName) + "' -ErrorAction Stop }; exit 0",
            cancellationToken);
    }

    private static void ValidateRule(DeviceControlRule rule)
    {
        if (rule.UploadLimitMbps is <= 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(rule), "Speed limits must be from 1 to 10,000 Mbps.");
        }

        if (!rule.BlockInternet && rule.UploadLimitMbps is null)
        {
            throw new ArgumentException("Set an upload limit or turn on Block internet.", nameof(rule));
        }
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
        if (!process.Start()) throw new InvalidOperationException("Windows could not start its local traffic policy command.");
        await process.WaitForExitAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "Windows rejected the local traffic policy command."
                : error.Trim());
        }
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
