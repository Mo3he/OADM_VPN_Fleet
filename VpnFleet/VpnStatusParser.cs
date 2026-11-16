using System.Text.Json;
using System.Text.RegularExpressions;

namespace VpnFleet;

/// <summary>What a VPN app reports about its tunnel.</summary>
public sealed record VpnStatus(VpnState State, string? Address, string? Detail);

/// <summary>
/// Reads the status each app publishes: Tailscale and ZeroTier and OpenVPN a <c>status.json</c>, NetBird
/// <c>api/status</c>, WireGuard its log lines. Pure functions of the text, so they are tested without a device.
/// </summary>
public static partial class VpnStatusParser
{
    private static readonly VpnStatus NoStatus = new(VpnState.Unknown, null, "The app has not reported its status yet.");

    public static VpnStatus Parse(VpnKind kind, string? text) => kind switch
    {
        VpnKind.Tailscale => Tailscale(text),
        VpnKind.NetBird => NetBird(text),
        VpnKind.ZeroTier => ZeroTier(text),
        VpnKind.WireGuard => WireGuard(text),
        VpnKind.OpenVpn => OpenVpn(text),
        VpnKind.WireGuardServer => WireGuardServer(text),
        _ => NoStatus,
    };

    /// <summary>The WireGuard Server app's <c>/status</c>: running, turned off or an error, and its peers.</summary>
    public static VpnStatus WireGuardServer(string? json)
    {
        if (Parse(json) is not { } root)
        {
            return NoStatus;
        }

        var address = WithoutPrefix(String(root, "address"));
        var peers = root.TryGetProperty("peers", out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().ToList() : [];
        var connected = peers.Count(e => e.ValueKind == JsonValueKind.Object && Bool(e, "connected"));
        return String(root, "state") switch
        {
            "running" => new VpnStatus(VpnState.Connected, address, $"{connected} of {peers.Count} peers connected"),
            "disabled" => new VpnStatus(VpnState.NotConnected, address, "The server is turned off."),
            "error" => new VpnStatus(VpnState.NotConnected, address, String(root, "error") ?? "The server reported an error."),
            _ => NoStatus,
        };
    }

    /// <summary><c>tailscale status --json</c>, as the app writes it to status.json.</summary>
    public static VpnStatus Tailscale(string? json)
    {
        if (Parse(json) is not { } root)
        {
            return NoStatus;
        }

        var backend = String(root, "BackendState");
        var self = root.TryGetProperty("Self", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        var online = self.ValueKind == JsonValueKind.Object && self.TryGetProperty("Online", out var o) && o.ValueKind == JsonValueKind.True;
        string? ip = null;
        if (self.ValueKind == JsonValueKind.Object && self.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
        {
            var all = ips.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
            ip = all.FirstOrDefault(a => !a.Contains(':', StringComparison.Ordinal)) ?? all.FirstOrDefault();
        }

        var node = self.ValueKind == JsonValueKind.Object ? String(self, "HostName") : null;
        var tailnet = root.TryGetProperty("CurrentTailnet", out var t) && t.ValueKind == JsonValueKind.Object ? String(t, "Name") : null;
        var detail = Join(node, tailnet);
        return backend switch
        {
            "Running" when online => new VpnStatus(VpnState.Connected, ip, detail),
            "Running" => new VpnStatus(VpnState.Connecting, ip, "Logged in, not online yet."),
            "NeedsLogin" => new VpnStatus(VpnState.NotSetUp, null, "Needs an auth key."),
            "NeedsMachineAuth" => new VpnStatus(VpnState.Connecting, null, "Waiting for approval in the Tailscale admin console."),
            "Stopped" => new VpnStatus(VpnState.NotConnected, null, "Stopped."),
            _ => new VpnStatus(VpnState.Connecting, null, null),
        };
    }

    /// <summary>NetBird's <c>api/status</c>; anything else (a proxy error page) means the client is not running.</summary>
    public static VpnStatus NetBird(string? json)
    {
        if (Parse(json) is not { } root)
        {
            return new VpnStatus(VpnState.NotSetUp, null, "Not running. Needs a setup key.");
        }

        var ip = WithoutPrefix(String(root, "overlay_ip"));
        var error = String(root, "error");
        if (Bool(root, "connected"))
        {
            return new VpnStatus(VpnState.Connected, ip, String(root, "management_url"));
        }

        return Bool(root, "management_connected") || Bool(root, "signal_connected")
            ? new VpnStatus(VpnState.Connecting, ip, error)
            : new VpnStatus(VpnState.NotConnected, ip, error);
    }

    /// <summary>ZeroTier's <c>status.json</c> written by the app's proxy.</summary>
    public static VpnStatus ZeroTier(string? json)
    {
        if (Parse(json) is not { } root)
        {
            return NoStatus;
        }

        var ip = WithoutPrefix(String(root, "zt_ip"));
        var network = String(root, "network_id");
        return String(root, "state") switch
        {
            "connected" => new VpnStatus(VpnState.Connected, ip, Join(network is null ? null : "Network " + network, null)),
            "waiting_config" => new VpnStatus(VpnState.NotSetUp, null, "Needs a network ID."),
            "waiting_auth" => new VpnStatus(VpnState.Connecting, null, "Waiting for approval in the network's controller."),
            "starting" => new VpnStatus(VpnState.Connecting, null, null),
            "disconnected" => new VpnStatus(VpnState.NotConnected, null, null),
            _ => NoStatus,
        };
    }

    /// <summary>OpenVPN's <c>status.json</c> written by the app's probe.</summary>
    public static VpnStatus OpenVpn(string? json)
    {
        if (Parse(json) is not { } root)
        {
            return NoStatus;
        }

        var ip = String(root, "vpn_ip4");
        var error = String(root, "last_error");
        return String(root, "state") switch
        {
            "connected" => new VpnStatus(VpnState.Connected, ip, null),
            "starting" or "connecting" or "tun_established" => new VpnStatus(VpnState.Connecting, ip, error),
            "waiting_config" => new VpnStatus(VpnState.NotSetUp, null, "Needs a VPN profile."),
            "config_error" => new VpnStatus(VpnState.NotConnected, null, error ?? "The VPN profile is not valid."),
            "auth_failed" => new VpnStatus(VpnState.NotConnected, null, error ?? "The server refused the user name or password."),
            "disconnected" => new VpnStatus(VpnState.NotConnected, null, error),
            _ => NoStatus,
        };
    }

    /// <summary>
    /// The WireGuard app has no status file: like its own page, read the log lines. Only a handshake proves the tunnel
    /// works; "tunnel up" only means it started.
    /// </summary>
    public static VpnStatus WireGuard(string? log)
    {
        var lines = (log ?? "").Split('\n');
        int up = -1, ok = -1, lost = -1, down = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("WireGuard tunnel up", StringComparison.Ordinal))
            {
                up = i;
            }

            if (line.Contains("WireGuard handshake ok", StringComparison.Ordinal))
            {
                ok = i;
            }

            if (line.Contains("WireGuard handshake lost", StringComparison.Ordinal))
            {
                lost = i;
            }

            if (DownLine().IsMatch(line))
            {
                down = i;
            }
        }

        if (up == -1 && ok == -1 && lost == -1 && down == -1)
        {
            return log is not null && log.Contains("config incomplete", StringComparison.Ordinal)
                ? new VpnStatus(VpnState.NotSetUp, null, "The tunnel settings are incomplete.")
                : new VpnStatus(VpnState.Unknown, null, "No tunnel events in the device log.");
        }

        string? ip = null, endpoint = null;
        if (up != -1)
        {
            ip = WithoutPrefix(IpField().Match(lines[up]) is { Success: true } m ? m.Groups[1].Value : null);
            endpoint = EndpointField().Match(lines[up]) is { Success: true } e ? e.Groups[1].Value : null;
        }

        var recent = string.Join('\n', lines.TakeLast(20));
        if (down > up && down != -1)
        {
            return new VpnStatus(VpnState.NotConnected, null, "Stopped.");
        }

        if (ok > up && ok > down && !(lost > ok))
        {
            return new VpnStatus(VpnState.Connected, ip, endpoint is null ? null : "Server " + endpoint);
        }

        if (lost > ok)
        {
            return new VpnStatus(VpnState.NotConnected, ip, "The server stopped answering.");
        }

        if (recent.Contains("level=ERROR", StringComparison.Ordinal))
        {
            return new VpnStatus(VpnState.NotConnected, ip, "The app reported an error. See the app log.");
        }

        if (log!.Contains("config incomplete", StringComparison.Ordinal))
        {
            return new VpnStatus(VpnState.NotSetUp, null, "The tunnel settings are incomplete.");
        }

        return new VpnStatus(VpnState.Connecting, ip, "Waiting for the server to answer.");
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? WithoutPrefix(string? address) => address is null ? null : address.Split('/')[0];

    private static string? Join(string? a, string? b) => (a, b) switch
    {
        (null, null) => null,
        (_, null) => a,
        (null, _) => b,
        _ => a + ", " + b,
    };

    [GeneratedRegex("shutting down|Deactivated successfully|acapctl.*stop")]
    private static partial Regex DownLine();

    [GeneratedRegex(@"ip=(\S+)")]
    private static partial Regex IpField();

    [GeneratedRegex(@"endpoint=(\S+)")]
    private static partial Regex EndpointField();
}
