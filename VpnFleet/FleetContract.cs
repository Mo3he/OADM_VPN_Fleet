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
    string? Error);

/// <summary>Everything the page shows. Version goes up with every change, so the page can ignore older states.</summary>
public sealed record FleetState(int Version, bool Reading, IReadOnlyList<FleetRow> Rows);

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

/// <summary>The contract between the server part and the page: method names, the event topic and the JSON format.</summary>
public static class Fleet
{
    public const string PluginId = "mo3he.vpn-fleet";

    public const string GetState = "getState";
    public const string Refresh = "refresh";
    public const string Connect = "connect";
    public const string GetPeers = "getPeers";
    public const string StateTopic = "state";

    /// <summary>The permission to change VPN settings on devices.</summary>
    public const string ConnectPermission = "connect";

    public const string NothingChosen = "Choose devices first.";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
