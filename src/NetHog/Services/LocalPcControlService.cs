using System.Diagnostics;
using System.IO;
using System.Net;
using NetHog.Models;

namespace NetHog.Services;

/// <summary>
/// Applies controls to the current PC without routing its traffic through the
/// peer ARP engine. Windows uses QoS and Windows Firewall. Linux uses a
/// NetHog-owned tc root qdisc for outbound shaping and a dedicated nftables or
/// iptables rule for outbound IPv4 blocking.
/// </summary>
public sealed class LocalPcControlService
{
    private const string QosPolicyName = "NetHog - Current PC upload limit";
    private const string FirewallRuleName = "NetHog - Current PC internet block";
    private const string LinuxQdiscHandle = "f00:";
    private const string LinuxNftTable = "nethog_local";
    private const string LinuxNftChain = "output";
    private const string LinuxIptablesComment = "NetHog current PC internet block";

    private string? _linuxInterfaceName;
    private string? _linuxNetworkCidr;

    public bool IsSupported => OperatingSystem.IsWindows()
                               || OperatingSystem.IsLinux() && HasLinuxTrafficTooling();

    public async Task ApplyRuleAsync(
        DeviceControlRule rule,
        CancellationToken cancellationToken = default) =>
        await ApplyRuleAsync(rule, null, cancellationToken);

    public async Task ApplyRuleAsync(
        DeviceControlRule rule,
        NetworkSnapshot? network,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (OperatingSystem.IsWindows())
        {
            await ApplyWindowsRuleAsync(rule, cancellationToken);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            await ApplyLinuxRuleAsync(rule, network, cancellationToken);
            return;
        }

        throw new PlatformNotSupportedException("Current-PC controls require Windows or Linux.");
    }

    public Task RemoveRuleAsync(CancellationToken cancellationToken = default) =>
        RemoveRuleAsync(null, cancellationToken);

    public async Task RemoveRuleAsync(
        NetworkSnapshot? network,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            await RunPowerShellAsync(
                $"$qos = Get-NetQosPolicy -Name '{EscapePowerShell(QosPolicyName)}' -ErrorAction SilentlyContinue; " +
                "if ($null -ne $qos) { Remove-NetQosPolicy -Name '" + EscapePowerShell(QosPolicyName) + "' -Confirm:$false -ErrorAction Stop }; " +
                $"$firewall = Get-NetFirewallRule -DisplayName '{EscapePowerShell(FirewallRuleName)}' -ErrorAction SilentlyContinue; " +
                "if ($null -ne $firewall) { Remove-NetFirewallRule -DisplayName '" + EscapePowerShell(FirewallRuleName) + "' -ErrorAction Stop }; exit 0",
                cancellationToken);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            await RemoveLinuxRuleAsync(network, cancellationToken);
        }
    }

    private async Task ApplyWindowsRuleAsync(
        DeviceControlRule rule,
        CancellationToken cancellationToken)
    {
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
                    $"New-NetQosPolicy -Name '{EscapePowerShell(QosPolicyName)}' -AppPathNameMatchCondition '*' -NetworkProfile All -ThrottleRateActionBitsPerSecond {bitsPerSecond} -PolicyStore ActiveStore | Out-Null",
                    cancellationToken);
            }

            if (rule.BlockInternet)
            {
                await RunPowerShellAsync(
                    $"New-NetFirewallRule -DisplayName '{EscapePowerShell(FirewallRuleName)}' -Direction Outbound -Action Block -Profile Any -RemoteAddress '0.0.0.0/0','::/0' -Protocol Any | Out-Null",
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

    private async Task ApplyLinuxRuleAsync(
        DeviceControlRule rule,
        NetworkSnapshot? network,
        CancellationToken cancellationToken)
    {
        if (network is null || string.IsNullOrWhiteSpace(network.InterfaceName))
        {
            throw new InvalidOperationException("Scan the active Linux network before configuring the current PC.");
        }
        if (rule.DownloadLimitMbps is not null)
        {
            throw new InvalidOperationException(
                "Linux local controls can shape this PC's outbound traffic here, but inbound shaping needs an IFB adapter. Leave Download blank and set Upload instead.");
        }
        ValidateRule(rule);
        if (rule.UploadLimitMbps is not null && FindCommand("tc") is null)
        {
            throw new InvalidOperationException("Linux upload shaping requires the tc command from iproute2.");
        }
        if (rule.BlockInternet && !HasLinuxFirewallTooling())
        {
            throw new InvalidOperationException("Linux internet blocking requires nftables or iptables to be installed.");
        }

        _linuxInterfaceName = network.InterfaceName;
        _linuxNetworkCidr = FormatNetworkCidr(network.LocalAddress, network.PrefixLength);
        await RemoveLinuxRuleAsync(network, cancellationToken);
        try
        {
            if (rule.UploadLimitMbps is { } upload)
            {
                await RunLinuxCommandAsync(
                    "tc",
                    ["qdisc", "add", "dev", network.InterfaceName, "root", "handle", LinuxQdiscHandle, "htb", "default", "1"],
                    cancellationToken);
                await RunLinuxCommandAsync(
                    "tc",
                    ["class", "add", "dev", network.InterfaceName, "parent", LinuxQdiscHandle,
                     "classid", "f00:1", "htb", "rate", $"{upload}mbit", "ceil", $"{upload}mbit"],
                    cancellationToken);
            }

            if (rule.BlockInternet)
            {
                await AddLinuxFirewallRuleAsync(network.InterfaceName, _linuxNetworkCidr!, cancellationToken);
            }
        }
        catch
        {
            try { await RemoveLinuxRuleAsync(network, CancellationToken.None); }
            catch { /* preserve the original policy error */ }
            throw;
        }
    }

    private async Task RemoveLinuxRuleAsync(
        NetworkSnapshot? network,
        CancellationToken cancellationToken)
    {
        var interfaceName = network?.InterfaceName ?? _linuxInterfaceName;
        var networkCidr = network is null
            ? _linuxNetworkCidr
            : FormatNetworkCidr(network.LocalAddress, network.PrefixLength);
        if (!string.IsNullOrWhiteSpace(interfaceName) && FindCommand("tc") is not null)
        {
            var qdisc = await RunLinuxCommandAsync(
                "tc", ["qdisc", "show", "dev", interfaceName], cancellationToken, ignoreFailure: true);
            if (qdisc.Contains($"qdisc htb {LinuxQdiscHandle}", StringComparison.OrdinalIgnoreCase))
            {
                await RunLinuxCommandAsync(
                    "tc", ["qdisc", "del", "dev", interfaceName, "root"], cancellationToken, ignoreFailure: true);
            }
        }

        if (FindCommand("nft") is not null)
        {
            await RunLinuxCommandAsync(
                "nft", ["delete", "table", "inet", LinuxNftTable], cancellationToken, ignoreFailure: true);
        }
        if (FindCommand("iptables") is not null && !string.IsNullOrWhiteSpace(interfaceName)
            && !string.IsNullOrWhiteSpace(networkCidr))
        {
            await RunLinuxCommandAsync(
                "iptables",
                ["-D", "OUTPUT", "-o", interfaceName, "!", "-d", networkCidr, "-m", "comment",
                 "--comment", LinuxIptablesComment, "-j", "DROP"],
                cancellationToken,
                ignoreFailure: true);
        }
    }

    private static async Task AddLinuxFirewallRuleAsync(
        string interfaceName,
        string networkCidr,
        CancellationToken cancellationToken)
    {
        if (FindCommand("nft") is not null)
        {
            await RunLinuxCommandAsync(
                "nft",
                ["add", "table", "inet", LinuxNftTable],
                cancellationToken);
            await RunLinuxCommandAsync(
                "nft",
                ["add", "chain", "inet", LinuxNftTable, LinuxNftChain,
                 "{ type filter hook output priority 5; policy accept; }"],
                cancellationToken);
            await RunLinuxCommandAsync(
                "nft",
                ["add", "rule", "inet", LinuxNftTable, LinuxNftChain,
                 "oifname", interfaceName, "ip", "daddr", "!=", networkCidr, "counter", "drop"],
                cancellationToken);
            return;
        }

        await RunLinuxCommandAsync(
            "iptables",
            ["-I", "OUTPUT", "-o", interfaceName, "!", "-d", networkCidr, "-m", "comment",
             "--comment", LinuxIptablesComment, "-j", "DROP"],
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

    private static bool HasLinuxTrafficTooling() =>
        FindCommand("tc") is not null || HasLinuxFirewallTooling();

    private static bool HasLinuxFirewallTooling() =>
        FindCommand("nft") is not null || FindCommand("iptables") is not null;

    private static string? FindCommand(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/usr/sbin", "/sbin", "/usr/bin", "/bin"]);
        return directories
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    private static async Task<string> RunLinuxCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool ignoreFailure = false)
    {
        var executable = FindCommand(fileName) ?? fileName;
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Linux could not start {fileName}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (!ignoreFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Linux rejected the {fileName} policy command."
                : error.Trim());
        }
        return output;
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
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "Windows rejected the local traffic policy command."
                : error.Trim());
        }
    }

    private static string FormatNetworkCidr(string addressText, int prefixLength)
    {
        if (!IPAddress.TryParse(addressText, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || prefixLength is < 0 or > 32)
        {
            throw new InvalidOperationException("NetHog could not determine the current Linux IPv4 subnet.");
        }

        var bytes = address.GetAddressBytes();
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var value = ((uint)bytes[0] << 24)
                    | ((uint)bytes[1] << 16)
                    | ((uint)bytes[2] << 8)
                    | bytes[3];
        var network = value & mask;
        var networkAddress = new IPAddress([
            (byte)(network >> 24),
            (byte)(network >> 16),
            (byte)(network >> 8),
            (byte)network]);
        return $"{networkAddress}/{prefixLength}";
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
