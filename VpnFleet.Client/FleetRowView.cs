namespace VpnFleet.Client;

/// <summary>A row of the table as the page shows it: the texts and the colour of the status chip.</summary>
public sealed record FleetRowView(FleetRow Row)
{
    public string Address => Row.Address;

    public string Model => Row.Model;

    public string Vpn => Row.Vpn is { } kind ? VpnApps.NameOf(kind) : "";

    public string AppVersion => Row.AppVersion ?? "";

    public string VpnAddress => Row.VpnAddress ?? "";

    public string Detail => Row.Error ?? Row.Detail ?? "";

    public string StateText => Row.Error is not null ? "Not read" : TextOf(Row.State);

    public bool IsOk => Row.Error is null && Row.State == VpnState.Connected;

    public bool IsWarning => Row.Error is null && Row.State is VpnState.Connecting or VpnState.NotSetUp or VpnState.AppStopped;

    public bool IsError => Row.Error is not null || Row.State == VpnState.NotConnected;

    public static string TextOf(VpnState state) => state switch
    {
        VpnState.Connected => "Connected",
        VpnState.Connecting => "Connecting",
        VpnState.NotConnected => "Not connected",
        VpnState.NotSetUp => "Not set up",
        VpnState.AppStopped => "App stopped",
        VpnState.NoVpnApp => "No VPN app",
        _ => "Unknown",
    };
}

/// <summary>A choice of the VPN list.</summary>
public sealed record VpnChoice(VpnKind Kind, string Name)
{
    public override string ToString() => Name;
}
