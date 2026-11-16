namespace VpnFleet;

/// <summary>A VPN app the page knows: its package name on the device and the name users see.</summary>
public sealed record VpnApp(VpnKind Kind, string AppName, string DisplayName)
{
    /// <summary>The device path of the app's files and API (<c>/local/&lt;app&gt;/</c>).</summary>
    public string LocalPath(string relative) => "/local/" + AppName + "/" + relative;

    /// <summary>A parameter of the app for param.cgi ("root.Tailscale_VPN.AuthKey").</summary>
    public string Parameter(string name) => "root." + AppName + "." + name;
}

/// <summary>The VPN apps of github.com/Mo3he, by kind and by package name.</summary>
public static class VpnApps
{
    public static readonly VpnApp Tailscale = new(VpnKind.Tailscale, "Tailscale_VPN", "Tailscale");
    public static readonly VpnApp NetBird = new(VpnKind.NetBird, "NetBird_VPN", "NetBird");
    public static readonly VpnApp ZeroTier = new(VpnKind.ZeroTier, "ZeroTier_VPN", "ZeroTier");
    public static readonly VpnApp WireGuard = new(VpnKind.WireGuard, "wireguardconfig", "WireGuard");
    public static readonly VpnApp OpenVpn = new(VpnKind.OpenVpn, "OpenVPN_VPN", "OpenVPN");

    /// <summary>The hub app: a camera that is the WireGuard server of the others.</summary>
    public const string WireGuardServerAppName = "WireGuard_Server";

    public static IReadOnlyList<VpnApp> All { get; } = [Tailscale, NetBird, ZeroTier, WireGuard, OpenVpn];

    public static VpnApp Of(VpnKind kind) => All.First(a => a.Kind == kind);

    public static VpnApp? ByAppName(string? appName) =>
        All.FirstOrDefault(a => string.Equals(a.AppName, appName, StringComparison.OrdinalIgnoreCase));

    public static string NameOf(VpnKind kind) => Of(kind).DisplayName;
}
