using System.Text.Json;
using System.Text.Json.Serialization;

namespace VpnFleet;

/// <summary>The VPN apps the page knows.</summary>
public enum VpnKind
{
    Tailscale,
    NetBird,
    ZeroTier,
    WireGuard,
    OpenVpn,
    WireGuardServer,
}

/// <summary>What a VPN app on a device is doing, from its own status.</summary>
public enum VpnState
{
    Connected,
    Connecting,
    NotConnected,
    NotSetUp,
    AppStopped,
    NoVpnApp,
    Unknown,
}

/// <summary>
/// One row of the page: a VPN app on a device, or a device without any (<see cref="VpnState.NoVpnApp"/>), or a device
/// that could not be read (<see cref="Error"/>).
/// </summary>
public sealed record FleetRow(
    Guid DeviceId,
    string Address,
    string Model,
    VpnKind? Vpn,
    string? AppVersion,
    VpnState State,
    string? VpnAddress,
    string? Detail,
    string? Error)
{
    /// <summary>The newest release of the app on GitHub; null when not known.</summary>
    public string? LatestVersion { get; init; }

    public bool UpdateAvailable => AppVersion is not null && LatestVersion is not null && AppVersions.IsNewer(LatestVersion, AppVersion);
}

/// <summary>Everything the page shows. Version goes up with every change, so the page can ignore older states.</summary>
public sealed record FleetState(int Version, bool Reading, IReadOnlyList<FleetRow> Rows)
{
    /// <summary>Why the newest versions are not known (GitHub not reachable); null when they are.</summary>
    public string? ReleaseError { get; init; }
}

/// <summary>What the page asks to read: every device of the user, or the chosen ones.</summary>
public sealed record RefreshRequest(bool All, IReadOnlyList<Guid> DeviceIds);

/// <summary>
/// Connect the chosen devices to a VPN. Only the fields of <see cref="Kind"/> are read. The request holds secrets (keys,
/// passwords): it stays in memory and is never stored or logged.
/// </summary>
public sealed record ConnectRequest
{
    public VpnKind Kind { get; init; }

    public bool All { get; init; }

    public IReadOnlyList<Guid> DeviceIds { get; init; } = [];

    // Tailscale
    public string? AuthKey { get; init; }

    public string? LoginServer { get; init; }

    // NetBird
    public string? SetupKey { get; init; }

    public string? ManagementUrl { get; init; }

    // ZeroTier
    public string? NetworkId { get; init; }

    // OpenVPN
    public string? Profile { get; init; }

    public string? UserName { get; init; }

    public string? Password { get; init; }

    // WireGuard: a camera running WireGuard Server (HubDeviceId), or any other server (the three fields below).
    public Guid? HubDeviceId { get; init; }

    public string? Endpoint { get; init; }

    public string? ServerPublicKey { get; init; }

    public string? AddressPool { get; init; }

    /// <summary>Optional: the networks the cameras reach through the tunnel; empty = the tunnel network.</summary>
    public string? AllowedIps { get; init; }
}

/// <summary>The answer to <see cref="Fleet.Connect"/>: how many devices got a task.</summary>
public sealed record ConnectReply(int Devices, IReadOnlyList<Guid> TaskIds);

/// <summary>The [Peer] sections of the cameras connected to another WireGuard server, for its configuration.</summary>
public sealed record PeersReply(int Count, string Text);

/// <summary>The newest release of an app; <see cref="Error"/> when GitHub could not be read.</summary>
public sealed record ReleaseInfo(VpnKind Kind, string? Version, string? PageUrl, DateTimeOffset? Published, string? Error);

public sealed record ReleasesRequest(bool CheckNow);

/// <summary>Install the newest release of an app on devices, or upgrade it. One task per device.</summary>
public sealed record InstallRequest(VpnKind Kind, IReadOnlyList<Guid> DeviceIds, bool Upgrade);

public sealed record InstallReply(int Devices, string Version, IReadOnlyList<Guid> TaskIds);

/// <summary>Read the settings of one app on one device.</summary>
public sealed record SettingsRequest(Guid DeviceId, VpnKind Kind);

/// <summary>
/// The settings of one app on one device. Secrets come without their value (<see cref="SettingValue.HasValue"/>):
/// they never leave the server.
/// </summary>
public sealed record SettingsReply(Guid DeviceId, VpnKind Kind, IReadOnlyList<SettingValue> Fields)
{
    /// <summary>WireGuard: the public key of the camera's (or hub's) private key, for the other side.</summary>
    public string? PublicKey { get; init; }
}

public sealed record SettingValue(string Name, string Label, SettingKind Kind, string Value, bool HasValue)
{
    public string? Hint { get; init; }

    public string? Placeholder { get; init; }

    /// <summary>Bool: the stored values for on and off. Choice: the stored values and their texts.</summary>
    public IReadOnlyList<SettingOption> Options { get; init; } = [];
}

public sealed record SettingOption(string Value, string Text);

/// <summary>
/// Change settings of one app on one device: only the changed fields. A secret left out keeps its value. The request
/// holds secrets: it stays in memory and is never stored or logged.
/// </summary>
public sealed record SaveSettingsRequest(Guid DeviceId, VpnKind Kind, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>OpenVPN: a new profile; null keeps the one on the device.</summary>
    public string? Profile { get; init; }
}

public sealed record SaveSettingsReply(int Changed, string? Note);

/// <summary>The contract between the server part and the page: method names, the event topic and the JSON format.</summary>
public static class Fleet
{
    public const string PluginId = "mo3he.vpn-fleet";

    public const string GetState = "getState";
    public const string Refresh = "refresh";
    public const string Connect = "connect";
    public const string GetPeers = "getPeers";
    public const string GetReleases = "getReleases";
    public const string Install = "install";
    public const string GetSettings = "getSettings";
    public const string SaveSettings = "saveSettings";
    public const string StateTopic = "state";

    /// <summary>The permission to change VPN settings on devices.</summary>
    public const string ConnectPermission = "connect";

    /// <summary>The permission to install and upgrade the VPN apps.</summary>
    public const string InstallPermission = "install";

    public const string NothingChosen = "Choose devices first.";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
