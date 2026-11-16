namespace VpnFleet;

/// <summary>An app the page knows: its package name on the device, the name users see and its GitHub repository.</summary>
public sealed record VpnApp(VpnKind Kind, string AppName, string DisplayName, string Repository, bool CanConnect = true)
{
    /// <summary>The device path of the app's files and API (<c>/local/&lt;app&gt;/</c>).</summary>
    public string LocalPath(string relative) => "/local/" + AppName + "/" + relative;

    /// <summary>A parameter of the app for param.cgi ("root.Tailscale_VPN.AuthKey").</summary>
    public string Parameter(string name) => "root." + AppName + "." + name;
}

/// <summary>The VPN apps of github.com/Mo3he, by kind and by package name.</summary>
public static class VpnApps
{
    public const string Owner = "Mo3he";

    public static readonly VpnApp Tailscale = new(VpnKind.Tailscale, "Tailscale_VPN", "Tailscale", "Axis_Cam_Tailscale");
    public static readonly VpnApp NetBird = new(VpnKind.NetBird, "NetBird_VPN", "NetBird", "Axis_Cam_NetBird");
    public static readonly VpnApp ZeroTier = new(VpnKind.ZeroTier, "ZeroTier_VPN", "ZeroTier", "Axis_Cam_ZeroTier");
    public static readonly VpnApp WireGuard = new(VpnKind.WireGuard, "wireguardconfig", "WireGuard", "Axis_Cam_WireGuard");
    public static readonly VpnApp OpenVpn = new(VpnKind.OpenVpn, "OpenVPN_VPN", "OpenVPN", "Axis_Cam_OpenVPN");

    /// <summary>The hub app: a camera that is the WireGuard server of the others. Installed and set up like the others, not a client.</summary>
    public static readonly VpnApp WireGuardServer = new(VpnKind.WireGuardServer, "WireGuard_Server", "WireGuard Server", "Axis_Cam_WireGuard_Server", CanConnect: false);

    public const string WireGuardServerAppName = "WireGuard_Server";

    public static IReadOnlyList<VpnApp> All { get; } = [Tailscale, NetBird, ZeroTier, WireGuard, OpenVpn, WireGuardServer];

    public static VpnApp Of(VpnKind kind) => All.First(a => a.Kind == kind);

    public static VpnApp? ByAppName(string? appName) =>
        All.FirstOrDefault(a => string.Equals(a.AppName, appName, StringComparison.OrdinalIgnoreCase));

    public static string NameOf(VpnKind kind) => Of(kind).DisplayName;
}
