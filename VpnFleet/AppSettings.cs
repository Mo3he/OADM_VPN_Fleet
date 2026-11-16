using System.Text.RegularExpressions;

namespace VpnFleet;

public enum SettingKind
{
    Text,
    Secret,
    Bool,
    Choice,
}

/// <summary>A setting of an app as the page shows it: its parameter, label and kind.</summary>
public sealed record SettingField(string Name, string Label, SettingKind Kind = SettingKind.Text)
{
    public string? Hint { get; init; }

    public string? Placeholder { get; init; }

    public IReadOnlyList<SettingOption> Options { get; init; } = [];
}

/// <summary>
/// The settings of each app (the <c>paramConfig</c> of its manifest) with the texts of the page, in the order the page
/// shows them. A parameter of a newer app version that is not here still shows, under its own name.
/// </summary>
public static partial class AppSettings
{
    private const string KeepSecret = "Leave empty to keep the current one.";
    private const string ProxyHint = "Port of the local proxy on the camera.";

    private static readonly IReadOnlyList<SettingOption> TrueFalse = [new("true", "On"), new("false", "Off")];
    private static readonly IReadOnlyList<SettingOption> YesNo = [new("yes", "On"), new("no", "Off")];

    private static readonly Dictionary<VpnKind, IReadOnlyList<SettingField>> Fields = new()
    {
        [VpnKind.Tailscale] =
        [
            new("AuthKey", "Auth key", SettingKind.Secret) { Hint = "A key from the Tailscale admin console. " + KeepSecret },
            new("CustomServer", "Login server") { Hint = "Optional. Only for Headscale.", Placeholder = "https://headscale.example.com" },
            new("AcceptDNS", "Use the tailnet's DNS", SettingKind.Bool) { Options = TrueFalse },
            new("AcceptRoutes", "Use routes of other devices", SettingKind.Bool) { Options = TrueFalse },
            new("AdvertiseRoutes", "Share these networks") { Hint = "Optional. Networks the camera makes reachable.", Placeholder = "192.168.1.0/24" },
            new("ExitNode", "Exit node") { Hint = "Optional. Tailscale address or name." },
            new("ExitNodeAllowLAN", "Keep the local network reachable", SettingKind.Bool) { Options = TrueFalse, Hint = "With an exit node." },
            new("SendLogs", "Send diagnostic logs to Tailscale", SettingKind.Bool) { Options = TrueFalse },
            new("HttpProxyPort", "HTTP proxy port") { Hint = ProxyHint },
            new("Socks5Port", "SOCKS5 proxy port") { Hint = ProxyHint },
        ],
        [VpnKind.NetBird] =
        [
            new("SetupKey", "Setup key", SettingKind.Secret) { Hint = "A key from the NetBird dashboard. " + KeepSecret },
            new("ManagementURL", "Management server") { Placeholder = "https://api.netbird.io:443" },
            new("RoutingMode", "Route traffic", SettingKind.Choice)
            {
                Options = [new("off", "Off"), new("internet", "On, except the camera's own subnets"), new("full", "On, including the camera's own subnets")],
            },
            new("AcceptRoutes", "Use routes of other peers", SettingKind.Bool) { Options = YesNo },
            new("ForwardPorts", "Forwarded ports") { Hint = "Camera ports reachable through the VPN.", Placeholder = "80,443,554" },
            new("HTTPProxyPort", "HTTP proxy port") { Hint = ProxyHint },
            new("Socks5Port", "SOCKS5 proxy port") { Hint = ProxyHint },
            new("InboundSocks5Port", "Inbound SOCKS5 port") { Hint = "Reach the camera's network from the VPN." },
        ],
        [VpnKind.ZeroTier] =
        [
            new("NetworkID", "Network ID") { Hint = "16 characters. Approve the camera in the network's controller.", Placeholder = "8056c2e21c000001" },
            new("ManagedGateway", "Managed gateway") { Hint = "Optional." },
            new("ForwardPorts", "Forwarded ports") { Hint = "Camera ports reachable through the VPN.", Placeholder = "80,443,554" },
            new("HTTPProxyPort", "HTTP proxy port") { Hint = ProxyHint },
            new("SOCKS5ProxyPort", "SOCKS5 proxy port") { Hint = ProxyHint },
            new("PlanetFile", "Planet file") { Hint = "Optional. Only for your own ZeroTier roots." },
        ],
        [VpnKind.WireGuard] =
        [
            new("PrivateKey", "Private key", SettingKind.Secret) { Hint = KeepSecret },
            new("ClientIP", "Tunnel address") { Placeholder = "10.66.0.2/24" },
            new("Endpoint", "Server address") { Placeholder = "vpn.example.com:51820" },
            new("PeerPublicKey", "Server public key"),
            new("AllowedIPs", "Networks through the tunnel") { Placeholder = "10.66.0.0/24" },
            new("ForwardPorts", "Forwarded ports") { Hint = "Camera ports reachable through the VPN.", Placeholder = "80,443,554" },
            new("LocalForwards", "Local forwards") { Hint = "Optional." },
            new("ListenPort", "Listen port"),
            new("MTU", "MTU"),
            new("HTTPProxyPort", "HTTP proxy port") { Hint = ProxyHint },
            new("OutboundSOCKS5Port", "SOCKS5 proxy port") { Hint = ProxyHint },
        ],
        [VpnKind.OpenVpn] =
        [
            new("Username", "User name") { Hint = "Optional. Only when the server asks for one." },
            new("Password", "Password", SettingKind.Secret) { Hint = KeepSecret },
            new("ForwardPorts", "Forwarded ports") { Hint = "Camera ports reachable through the VPN.", Placeholder = "80,443,554" },
            new("HttpProxyPort", "HTTP proxy port") { Hint = ProxyHint },
            new("Socks5Port", "SOCKS5 proxy port") { Hint = ProxyHint },
        ],
        [VpnKind.WireGuardServer] =
        [
            new("Enabled", "Server on", SettingKind.Bool) { Options = YesNo },
            new("PublicEndpoint", "Public address") { Hint = "How the cameras reach this server.", Placeholder = "vpn.example.com:51820" },
            new("Address", "Tunnel address") { Placeholder = "10.66.0.1/24" },
            new("ListenPort", "Listen port"),
            new("Peers", "Peers") { Hint = "name,public key,address; separated by ;" },
            new("RouteSubnets", "Routed networks") { Hint = "Optional. Networks the peers reach through this camera, or LAN." },
            new("PeerToPeer", "Peers reach each other", SettingKind.Bool) { Options = YesNo },
            new("ClientDNS", "DNS for the peers") { Hint = "Optional." },
            new("MTU", "MTU"),
            new("PrivateKey", "Private key", SettingKind.Secret) { Hint = "Optional. Empty makes a new one. " + KeepSecret },
        ],
    };

    /// <summary>
    /// The fields of the app with the values of the device (param.cgi keys without "root.", e.g. "Tailscale_VPN.AuthKey").
    /// Parameters the device does not have are left out; ones this table does not know come last.
    /// </summary>
    public static IReadOnlyList<SettingValue> Read(VpnApp app, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(parameters);
        var prefix = app.AppName + ".";
        var values = parameters
            .Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(p => p.Key[prefix.Length..], p => p.Value, StringComparer.Ordinal);
        var known = Fields[app.Kind];
        var list = new List<SettingValue>();
        foreach (var field in known.Where(f => values.ContainsKey(f.Name)))
        {
            var value = values[field.Name];
            list.Add(new SettingValue(field.Name, field.Label, field.Kind, field.Kind == SettingKind.Secret ? "" : value, value.Length > 0)
            {
                Hint = field.Hint,
                Placeholder = field.Placeholder,
                Options = field.Options,
            });
        }

        var names = known.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, value) in values.Where(v => !names.Contains(v.Key) && !v.Key.Contains('.', StringComparison.Ordinal)).OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            var secret = LooksSecret(name);
            list.Add(new SettingValue(name, name, secret ? SettingKind.Secret : SettingKind.Text, secret ? "" : value, value.Length > 0));
        }

        return list;
    }

    /// <summary>
    /// The problems of a change, by field: only parameters the device has, one line each, the stored values of on/off and
    /// choices, and the forms the apps need (keys, network ID, addresses).
    /// </summary>
    public static IReadOnlyList<FieldError> Validate(VpnApp app, IReadOnlyDictionary<string, string> changes, IReadOnlyCollection<string> deviceNames)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(deviceNames);
        var errors = new List<FieldError>();
        var fields = Fields[app.Kind].ToDictionary(f => f.Name, StringComparer.Ordinal);
        foreach (var (name, raw) in changes)
        {
            var value = raw ?? "";
            string? problem = null;
            if (!deviceNames.Contains(name))
            {
                problem = "This app has no setting " + name + ".";
            }
            else if (value.Length > 4096 || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
            {
                problem = "Enter one line.";
            }
            else if (fields.TryGetValue(name, out var field) && field.Kind is SettingKind.Bool or SettingKind.Choice && !field.Options.Any(o => o.Value == value))
            {
                problem = "Choose one of the options.";
            }
            else
            {
                problem = FormatProblem(app.Kind, name, value.Trim());
            }

            if (problem is not null)
            {
                errors.Add(new FieldError(name, problem));
            }
        }

        return errors;
    }

    private static string? FormatProblem(VpnKind kind, string name, string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        return (kind, name) switch
        {
            (VpnKind.ZeroTier, "NetworkID") when !NetworkId().IsMatch(value) => "Enter the 16-character network ID.",
            (VpnKind.WireGuard or VpnKind.WireGuardServer, "PrivateKey") or (VpnKind.WireGuard, "PeerPublicKey") when !WireGuardKeys.IsKey(value) =>
                "Enter a key of 44 characters.",
            (VpnKind.WireGuard, "ClientIP") or (VpnKind.WireGuardServer, "Address") when WireGuardAddressing.ParseNetworks(value) is not { Count: 1 } =>
                "Enter an address with its network, such as 10.66.0.2/24.",
            (VpnKind.WireGuard, "AllowedIPs") when WireGuardAddressing.ParseNetworks(value) is null => "Enter networks such as 10.66.0.0/24, separated by commas.",
            (_, var n) when n.EndsWith("Port", StringComparison.Ordinal) && !(int.TryParse(value, out var port) && port is > 0 and < 65536) => "Enter a port from 1 to 65535.",
            _ => null,
        };
    }

    /// <summary>True for a parameter whose value is a secret, also of a newer app version this table does not know.</summary>
    public static bool IsSecret(VpnKind kind, string name) =>
        Fields[kind].FirstOrDefault(f => f.Name == name) is { } field ? field.Kind == SettingKind.Secret : LooksSecret(name);

    private static bool LooksSecret(string name) =>
        (name.Contains("Key", StringComparison.OrdinalIgnoreCase) && !name.Contains("Public", StringComparison.OrdinalIgnoreCase))
        || name.Contains("Password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Token", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[0-9a-fA-F]{16}$")]
    private static partial Regex NetworkId();
}
