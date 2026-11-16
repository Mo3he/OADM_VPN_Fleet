namespace VpnFleet.Tests;

public sealed class StatusAndRulesTests
{
    [Fact]
    public void Tailscale_is_connected_when_running_and_online()
    {
        var status = VpnStatusParser.Tailscale("""
            {"BackendState":"Running","Self":{"HostName":"axis-b8a44f","Online":true,"TailscaleIPs":["100.101.102.103","fd7a:115c::1"]},"CurrentTailnet":{"Name":"example.org"}}
            """);

        Assert.Equal(new VpnStatus(VpnState.Connected, "100.101.102.103", "axis-b8a44f, example.org"), status);
        Assert.Equal(VpnState.NotSetUp, VpnStatusParser.Tailscale("""{"BackendState":"NeedsLogin"}""").State);
        Assert.Equal(VpnState.Unknown, VpnStatusParser.Tailscale("<html>").State);
    }

    [Fact]
    public void NetBird_reads_its_status_and_an_error_page_means_not_running()
    {
        Assert.Equal(new VpnStatus(VpnState.Connected, "100.92.1.7", "https://api.netbird.io:443"),
            VpnStatusParser.NetBird("""{"connected":true,"management_connected":true,"signal_connected":true,"overlay_ip":"100.92.1.7/16","management_url":"https://api.netbird.io:443"}"""));
        Assert.Equal(new VpnStatus(VpnState.Connecting, null, "signal down"),
            VpnStatusParser.NetBird("""{"connected":false,"management_connected":true,"error":"signal down"}"""));
        Assert.Equal(VpnState.NotSetUp, VpnStatusParser.NetBird("<html><body>503 Service Unavailable</body></html>").State);
    }

    [Theory]
    [InlineData("""{"state":"connected","zt_ip":"10.147.17.5/24","network_id":"8056c2e21c000001"}""", VpnState.Connected, "10.147.17.5")]
    [InlineData("""{"state":"waiting_auth"}""", VpnState.Connecting, null)]
    [InlineData("""{"state":"waiting_config"}""", VpnState.NotSetUp, null)]
    public void ZeroTier_states(string json, VpnState state, string? address)
    {
        var status = VpnStatusParser.ZeroTier(json);
        Assert.Equal(state, status.State);
        Assert.Equal(address, status.Address);
    }

    [Theory]
    [InlineData("""{"state":"connected","vpn_ip4":"10.8.0.6"}""", VpnState.Connected)]
    [InlineData("""{"state":"tun_established"}""", VpnState.Connecting)]
    [InlineData("""{"state":"auth_failed","last_error":"AUTH_FAILED"}""", VpnState.NotConnected)]
    [InlineData("""{"state":"waiting_config"}""", VpnState.NotSetUp)]
    public void OpenVpn_states(string json, VpnState state) => Assert.Equal(state, VpnStatusParser.OpenVpn(json).State);

    [Fact]
    public void WireGuard_is_connected_only_after_a_handshake()
    {
        const string up = "2026-11-16T10:00:00 wireguardconfig[1]: level=INFO msg=\"WireGuard tunnel up\" ip=10.66.0.2/24 endpoint=vpn.example.com:51820\n";
        const string ok = "2026-11-16T10:00:02 wireguardconfig[1]: level=INFO msg=\"WireGuard handshake ok\"\n";
        const string lost = "2026-11-16T10:05:00 wireguardconfig[1]: level=WARN msg=\"WireGuard handshake lost\"\n";

        Assert.Equal(new VpnStatus(VpnState.Connecting, "10.66.0.2", "Waiting for the server to answer."), VpnStatusParser.WireGuard(up));
        Assert.Equal(new VpnStatus(VpnState.Connected, "10.66.0.2", "Server vpn.example.com:51820"), VpnStatusParser.WireGuard(up + ok));
        Assert.Equal(VpnState.NotConnected, VpnStatusParser.WireGuard(up + ok + lost).State);
        Assert.Equal(VpnState.NotSetUp, VpnStatusParser.WireGuard("level=WARN msg=\"config incomplete\"\n").State);
    }

    [Fact]
    public void Only_the_fields_of_the_chosen_VPN_count()
    {
        var request = new ConnectRequest { Kind = VpnKind.ZeroTier, NetworkId = "8056c2e21c000001", AuthKey = "not checked" };
        Assert.Empty(ConnectRules.Validate(request));

        var errors = ConnectRules.Validate(new ConnectRequest { Kind = VpnKind.Tailscale, AuthKey = "tskey with space", LoginServer = "headscale" });
        Assert.Equal([nameof(ConnectRequest.AuthKey), nameof(ConnectRequest.LoginServer)], errors.Select(e => e.Field));
    }

    [Fact]
    public void WireGuard_without_a_hub_needs_the_server()
    {
        var errors = ConnectRules.Validate(new ConnectRequest { Kind = VpnKind.WireGuard, Endpoint = "vpn.example.com", ServerPublicKey = "abc", AddressPool = "10.66.0.1" });
        Assert.Equal([nameof(ConnectRequest.Endpoint), nameof(ConnectRequest.ServerPublicKey), nameof(ConnectRequest.AddressPool)], errors.Select(e => e.Field));

        var ok = new ConnectRequest
        {
            Kind = VpnKind.WireGuard,
            Endpoint = "vpn.example.com:51820",
            ServerPublicKey = WireGuardKeys.Generate().PublicKey,
            AddressPool = "10.66.0.1/24",
            AllowedIps = "10.66.0.0/24, 192.168.1.0/24",
        };
        Assert.Empty(ConnectRules.Validate(ok));
        Assert.Empty(ConnectRules.Validate(new ConnectRequest { Kind = VpnKind.WireGuard, HubDeviceId = Guid.NewGuid() }));
    }

    [Fact]
    public void An_OpenVpn_profile_needs_a_remote_server()
    {
        Assert.Empty(ConnectRules.Validate(new ConnectRequest { Kind = VpnKind.OpenVpn, Profile = "client\ndev tun\nremote vpn.example.com 1194\n" }));
        Assert.Equal(nameof(ConnectRequest.Profile), Assert.Single(ConnectRules.Validate(new ConnectRequest { Kind = VpnKind.OpenVpn, Profile = "client\ndev tun\n" })).Field);
        Assert.Equal(nameof(ConnectRequest.UserName), Assert.Single(ConnectRules.Validate(new ConnectRequest { Kind = VpnKind.OpenVpn, Profile = "remote a 1194", Password = "x" })).Field);
    }
}
