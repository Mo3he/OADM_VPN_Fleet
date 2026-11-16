using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

using VpnFleet.Client;

namespace VpnFleet.Tests;

public sealed class ConnectTests
{
    private const string TailscaleOnline = """{"BackendState":"Running","Self":{"HostName":"cam","Online":true,"TailscaleIPs":["100.64.0.9"]}}""";

    // ---- The task on one device ----

    [Fact]
    public async Task Tailscale_gets_its_key_starts_and_connects()
    {
        var camera = new AppCamera().WithApp(VpnApps.Tailscale.AppName, status: "Stopped");
        camera.Files[VpnApps.Tailscale.LocalPath("status.json")] = TailscaleOnline;
        var request = new ConnectRequest { Kind = VpnKind.Tailscale, AuthKey = "tskey-auth-secret123" };

        var ctx = await RunTaskAsync(camera, new ConnectPayload { Settings = request });

        Assert.Equal("tskey-auth-secret123", camera.Parameters["root.Tailscale_VPN.AuthKey"]);
        Assert.False(camera.Parameters.ContainsKey("root.Tailscale_VPN.CustomServer")); // left empty: kept as it is
        Assert.Equal("Running", camera.Apps[0].Status);
        Assert.Equal(
            [$"{ConnectTaskPlugin.CheckStep}: Done", $"{ConnectTaskPlugin.SaveStep}: Done", $"{ConnectTaskPlugin.StartStep}: Done", $"{ConnectTaskPlugin.WaitStep}: Done", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
        Assert.Empty(ctx.Warnings);
        Assert.DoesNotContain(camera.Requests, r => r.Contains("secret123", StringComparison.Ordinal)); // never in a URL
        Assert.DoesNotContain(ctx.LogLines, l => l.Contains("secret123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_device_without_the_app_is_not_changed()
    {
        var camera = new AppCamera().WithApp(VpnApps.NetBird.AppName);
        var request = new ConnectRequest { Kind = VpnKind.ZeroTier, NetworkId = "8056C2E21C000001" };

        var ex = await Assert.ThrowsAsync<VpnDeviceException>(() => RunTaskAsync(camera, new ConnectPayload { Settings = request }));

        Assert.Contains("ZeroTier app is not installed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, camera.Writes);
    }

    [Fact]
    public async Task A_tunnel_that_does_not_come_up_ends_with_a_warning()
    {
        var camera = new AppCamera().WithApp(VpnApps.ZeroTier.AppName);
        camera.Files[VpnApps.ZeroTier.LocalPath("status.json")] = """{"state":"waiting_auth"}""";
        var request = new ConnectRequest { Kind = VpnKind.ZeroTier, NetworkId = "8056C2E21C000001" };

        var ctx = await RunTaskAsync(camera, new ConnectPayload { Settings = request }, wait: TimeSpan.Zero);

        Assert.Equal("8056c2e21c000001", camera.Parameters["root.ZeroTier_VPN.NetworkID"]);
        Assert.Contains($"{ConnectTaskPlugin.StartStep}: Skipped", StepRun.Lines(ctx.Steps));
        Assert.Contains("Waiting for approval", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenVpn_uploads_the_profile_before_the_settings()
    {
        var camera = new AppCamera().WithApp(VpnApps.OpenVpn.AppName);
        camera.Files[VpnApps.OpenVpn.LocalPath("status.json")] = """{"state":"connected","vpn_ip4":"10.8.0.6"}""";
        camera.Files[VpnApps.OpenVpn.LocalPath("api/profile")] = "OK";
        const string profile = "client\nremote vpn.example.com 1194\n";
        var request = new ConnectRequest { Kind = VpnKind.OpenVpn, Profile = profile, UserName = "cam", Password = "p&ss=word" };

        await RunTaskAsync(camera, new ConnectPayload { Settings = request });

        Assert.Equal([VpnApps.OpenVpn.LocalPath("api/profile"), DeviceVpn.ParamPath], camera.Posts.Select(p => p.Path));
        Assert.Equal(profile, camera.Posts[0].Body);
        Assert.Equal("p&ss=word", camera.Parameters["root.OpenVPN_VPN.Password"]);
    }

    [Fact]
    public async Task The_hub_gets_the_cameras_as_peers_and_keeps_the_others()
    {
        var hub = new AppCamera().WithApp(VpnApps.WireGuardServerAppName);
        var laptop = WireGuardKeys.Generate().PublicKey;
        hub.Parameters["WireGuard_Server.Peers"] = $"laptop,{laptop},10.66.0.2";
        hub.Parameters["WireGuard_Server.Enabled"] = "no";
        var peer = new HubPeer("B8A44F631339", WireGuardKeys.Generate().PublicKey, "10.66.0.3");

        var ctx = await RunTaskAsync(hub, new ConnectPayload { Settings = new ConnectRequest { Kind = VpnKind.WireGuard }, HubPeers = [peer] });

        Assert.Equal($"laptop,{laptop},10.66.0.2;B8A44F631339,{peer.PublicKey},10.66.0.3", hub.Parameters["root.WireGuard_Server.Peers"]);
        Assert.Contains("turned off", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
    }

    // ---- The page's methods ----

    [Fact]
    public async Task Refresh_shows_every_VPN_app_and_devices_without_one()
    {
        var (plugin, server) = await StartAsync();
        var withApps = new AppCamera().WithApp(VpnApps.Tailscale.AppName, version: "1.104.7").WithApp(VpnApps.NetBird.AppName, status: "Stopped").WithApp("Other_App");
        withApps.Files[VpnApps.Tailscale.LocalPath("status.json")] = TailscaleOnline;
        server.Add(new FakeDevice { Address = "10.0.0.48" }, withApps);
        server.Add(new FakeDevice { Address = "10.0.0.49" }, new AppCamera());
        server.Add(new FakeDevice { Address = "10.0.0.50", Status = DeviceStatus.CredentialsRequired }, new AppCamera());

        await CallAsync(server, plugin, Fleet.Refresh, new RefreshRequest(true, []));
        var state = await NextStateAsync(server, s => !s.Reading && s.Rows.Count > 0);

        Assert.Equal(
            ["10.0.0.48 Tailscale Connected 100.64.0.9", "10.0.0.48 NetBird AppStopped ", "10.0.0.49  NoVpnApp ", "10.0.0.50  Unknown "],
            state.Rows.Select(r => $"{r.Address} {r.Vpn} {r.State} {r.VpnAddress}"));
        Assert.Equal("1.104.7", state.Rows[0].AppVersion);
        Assert.Equal(DeviceMessages.CredentialsRequired, state.Rows[3].Error);
        Assert.Equal("3 devices: 1 connected, 1 not connected, 1 without a VPN app, 1 not read", VpnFleetViewModel.SummaryOf(state.Rows, false));
    }

    [Fact]
    public async Task Connect_starts_one_task_per_device_and_refuses_an_invalid_request()
    {
        var (plugin, server) = await StartAsync();
        var a = server.Add(new FakeDevice { Address = "10.0.0.48" }, new AppCamera());
        var b = server.Add(new FakeDevice { Address = "10.0.0.49" }, new AppCamera());
        var request = new ConnectRequest { Kind = VpnKind.NetBird, SetupKey = "A1B2C3D4-0000-4000-8000-000000000000", DeviceIds = [a.Id, b.Id] };

        var reply = JsonSerializer.Deserialize<ConnectReply>((await CallAsync(server, plugin, Fleet.Connect, request))!, Fleet.Json)!;

        var run = Assert.Single(server.TaskRuns);
        Assert.Equal(ConnectTaskPlugin.PluginId, run.PluginId);
        Assert.Equal([a.Id, b.Id], run.DeviceIds);
        Assert.Equal(2, reply.Devices);
        Assert.Equal("Connect to NetBird", plugin.ConnectTask.GetTaskName(run.PayloadJson));
        Assert.Equal("Connect 2 devices to NetBird", plugin.AuditDetail(Fleet.Connect, JsonSerializer.Serialize(request, Fleet.Json)));

        await Assert.ThrowsAsync<ArgumentException>(() => CallAsync(server, plugin, Fleet.Connect, request with { SetupKey = "" }));
        Assert.Single(server.TaskRuns);
    }

    [Fact]
    public async Task WireGuard_with_a_hub_makes_keys_and_addresses_and_remembers_only_public_keys()
    {
        var (plugin, server) = await StartAsync();
        var (hub, hubCamera, hubKey) = AddHub(server);
        var laptop = WireGuardKeys.Generate().PublicKey;
        hubCamera.Parameters["WireGuard_Server.Peers"] = $"laptop,{laptop},10.66.0.2";
        var a = server.Add(new FakeDevice { Address = "10.0.0.48", Serial = "ACCC8E000001" }, new AppCamera().WithApp(VpnApps.WireGuard.AppName));
        var b = server.Add(new FakeDevice { Address = "10.0.0.49", Serial = "ACCC8E000002" }, new AppCamera().WithApp(VpnApps.WireGuard.AppName));
        var request = new ConnectRequest { Kind = VpnKind.WireGuard, HubDeviceId = hub.Id, DeviceIds = [a.Id, b.Id, hub.Id] };

        await CallAsync(server, plugin, Fleet.Connect, request);

        Assert.Equal(2, server.TaskRuns.Count);
        var hubRun = JsonSerializer.Deserialize<ConnectPayload>(server.TaskRuns[0].PayloadJson!, Fleet.Json)!;
        Assert.Equal([hub.Id], server.TaskRuns[0].DeviceIds);
        Assert.Equal(["ACCC8E000001 10.66.0.3", "ACCC8E000002 10.66.0.4"], hubRun.HubPeers!.Select(p => $"{p.Name} {p.Address}"));
        Assert.Equal("Add 2 cameras to the WireGuard server", plugin.ConnectTask.GetTaskName(server.TaskRuns[0].PayloadJson));

        var cameraRun = JsonSerializer.Deserialize<ConnectPayload>(server.TaskRuns[1].PayloadJson!, Fleet.Json)!;
        Assert.Equal([a.Id, b.Id], server.TaskRuns[1].DeviceIds); // never the hub
        Assert.Equal("vpn.example.com:51820", cameraRun.Endpoint);
        Assert.Equal(hubKey, cameraRun.PeerPublicKey);
        Assert.Equal("10.66.0.0/24", cameraRun.AllowedIps);
        Assert.Equal("10.66.0.3/24", cameraRun.Cameras![a.Id].ClientIp);
        Assert.Equal(hubRun.HubPeers![0].PublicKey, WireGuardKeys.PublicKeyOf(cameraRun.Cameras[a.Id].PrivateKey));

        var stored = JsonSerializer.Deserialize<List<WireGuardRecord>>(server.PluginSettings.Values[VpnFleetPlugin.WireGuardSettingKey]!, Fleet.Json)!;
        Assert.Equal(hubRun.HubPeers.Select(p => p.PublicKey), stored.Select(r => r.PublicKey));

        // The camera's task saves its own key and the hub's values.
        var cameraA = (AppCamera)server.Cameras[a.Id];
        await RunTaskAsync(cameraA, cameraRun, plugin, a, wait: TimeSpan.Zero);
        Assert.Equal(cameraRun.Cameras[a.Id].PrivateKey, cameraA.Parameters["root.wireguardconfig.PrivateKey"]);
        Assert.Equal("10.66.0.3/24", cameraA.Parameters["root.wireguardconfig.ClientIP"]);
        Assert.Equal(hubKey, cameraA.Parameters["root.wireguardconfig.PeerPublicKey"]);

        // Connected again: same addresses, new keys.
        await CallAsync(server, plugin, Fleet.Connect, request with { DeviceIds = [b.Id] });
        var again = JsonSerializer.Deserialize<ConnectPayload>(server.TaskRuns[2].PayloadJson!, Fleet.Json)!;
        Assert.Equal("ACCC8E000002 10.66.0.4", $"{again.HubPeers![0].Name} {again.HubPeers[0].Address}");
        Assert.NotEqual(hubRun.HubPeers[1].PublicKey, again.HubPeers[0].PublicKey);
    }

    [Fact]
    public async Task A_hub_without_a_running_server_is_refused_before_anything_starts()
    {
        var (plugin, server) = await StartAsync();
        var (hub, hubCamera, _) = AddHub(server);
        hubCamera.Apps[0] = hubCamera.Apps[0] with { Status = "Stopped" };
        var a = server.Add(new FakeDevice { Address = "10.0.0.48" }, new AppCamera());

        var ex = await Assert.ThrowsAsync<VpnDeviceException>(() =>
            CallAsync(server, plugin, Fleet.Connect, new ConnectRequest { Kind = VpnKind.WireGuard, HubDeviceId = hub.Id, DeviceIds = [a.Id] }));

        Assert.Contains("not running", ex.Message, StringComparison.Ordinal);
        Assert.Empty(server.TaskRuns);
        Assert.False(server.PluginSettings.Values.ContainsKey(VpnFleetPlugin.WireGuardSettingKey));
    }

    [Fact]
    public async Task Another_WireGuard_server_gets_the_peer_sections_of_its_cameras()
    {
        var (plugin, server) = await StartAsync();
        var a = server.Add(new FakeDevice { Address = "10.0.0.48", Serial = "ACCC8E000001", Model = "M3086-V" }, new AppCamera());
        var request = new ConnectRequest
        {
            Kind = VpnKind.WireGuard,
            Endpoint = "vpn.example.com:51820",
            ServerPublicKey = WireGuardKeys.Generate().PublicKey,
            AddressPool = "10.9.0.0/24",
            AllowedIps = "10.9.0.0/24,192.168.1.0/24",
            DeviceIds = [a.Id],
        };

        await CallAsync(server, plugin, Fleet.Connect, request);
        var payload = JsonSerializer.Deserialize<ConnectPayload>(Assert.Single(server.TaskRuns).PayloadJson!, Fleet.Json)!;
        var peers = JsonSerializer.Deserialize<PeersReply>((await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.GetPeers, null))!, Fleet.Json)!;

        Assert.Equal("10.9.0.2/24", payload.Cameras![a.Id].ClientIp); // .1 is the server
        Assert.Equal("10.9.0.0/24,192.168.1.0/24", payload.AllowedIps);
        Assert.Equal(1, peers.Count);
        Assert.Equal(
            $"[Peer]\n# 10.0.0.48 M3086-V (ACCC8E000001)\nPublicKey = {WireGuardKeys.PublicKeyOf(payload.Cameras[a.Id].PrivateKey)}\nAllowedIPs = 10.9.0.2/32\n",
            peers.Text);
    }

    [Fact]
    public async Task Connect_needs_its_own_permission()
    {
        var (plugin, _) = await StartAsync();

        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(Fleet.Refresh));
        Assert.Equal(Fleet.ConnectPermission, plugin.RequiredPermission(Fleet.Connect));
        Assert.Equal(Fleet.InstallPermission, plugin.RequiredPermission(Fleet.Install));
        Assert.Equal(Fleet.ConnectPermission, plugin.RequiredPermission(Fleet.SaveSettings));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(Fleet.GetSettings));
        Assert.All(plugin.Permissions, p => Assert.False(p.OperatorDefault));
    }

    [Fact]
    public async Task The_page_hides_connect_without_the_permission()
    {
        var (plugin, server) = await StartAsync();
        var ctx = new FakePageContext(plugin, server);
        ctx.Denied.Add(PermissionKeys.Of(Fleet.PluginId, Fleet.ConnectPermission));
        using var page = new VpnFleetViewModel(ctx);

        Assert.False(page.MayConnect);
        Assert.False(page.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public void The_form_sends_only_what_the_chosen_server_needs()
    {
        var form = new ConnectFormViewModel { SelectedVpn = new VpnChoice(VpnKind.WireGuard, "WireGuard") };
        Assert.False(form.IsFormValid); // no hub chosen

        form.HubDeviceId = Guid.NewGuid();
        form.Endpoint = "ignored:1";
        Assert.True(form.IsFormValid);
        Assert.Null(form.ToRequest(false, []).Endpoint);

        form.UseHub = false;
        Assert.False(form.IsFormValid);
        Assert.Null(form.ToRequest(false, []).HubDeviceId);
    }

    /// <summary>The canary: a user whose group holds only "inside" calls every method with "outside" and with an unknown id.</summary>
    [Fact]
    public async Task No_method_reaches_a_device_outside_the_users_group()
    {
        var (plugin, server) = await StartAsync();
        var inside = server.Add(new FakeDevice { Address = "10.0.0.48" }, new AppCamera());
        var outside = server.Add(new FakeDevice { Address = "10.0.0.49" }, new AppCamera());

        await new PluginCanary(server, plugin, inside, outside)
            .Method(Fleet.Refresh, """{"all":false,"deviceIds":["{inside}","{device}"]}""")
            .Method(Fleet.Connect, """{"kind":"ZeroTier","networkId":"8056c2e21c000001","deviceIds":["{inside}","{device}"]}""")
            .Method(Fleet.Connect, """{"kind":"WireGuard","hubDeviceId":"{device}","deviceIds":["{inside}"]}""")
            .WithoutDevices(Fleet.GetState, "the table of the user's devices")
            .WithoutDevices(Fleet.GetPeers, "the peers of the user's devices")
            .WithoutDevices(Fleet.GetReleases, "the newest releases on GitHub", """{"checkNow":false}""")
            .Method(Fleet.GetSettings, """{"deviceId":"{device}","kind":"Tailscale"}""")
            .Method(Fleet.SaveSettings, """{"deviceId":"{device}","kind":"Tailscale","values":{"AcceptDNS":"true"}}""")
            .Method(Fleet.Install, """{"kind":"Tailscale","deviceIds":["{inside}","{device}"],"upgrade":false}""")
            .RunAsync();
    }

    // ---- Helpers ----

    private static (FakeDevice Hub, AppCamera Camera, string PublicKey) AddHub(FakeCoreContext server)
    {
        var key = WireGuardKeys.Generate().PublicKey;
        var camera = new AppCamera().WithApp(VpnApps.WireGuardServerAppName);
        camera.Parameters["WireGuard_Server.Address"] = "10.66.0.1/24";
        camera.Parameters["WireGuard_Server.ListenPort"] = "51820";
        camera.Parameters["WireGuard_Server.PublicEndpoint"] = "vpn.example.com";
        camera.Parameters["WireGuard_Server.Enabled"] = "yes";
        camera.Parameters["WireGuard_Server.Peers"] = "";
        camera.Files["/local/WireGuard_Server/status"] = $$"""{"state":"running","publicKey":"{{key}}","listenPort":51820}""";
        var hub = server.Add(new FakeDevice { Address = "10.0.0.10", Serial = "ACCC8E0000AA" }, camera);
        return (hub, camera, key);
    }

    private static async Task<FakeTaskContext> RunTaskAsync(AppCamera camera, ConnectPayload payload, VpnFleetPlugin? plugin = null, IDeviceInfo? device = null, TimeSpan? wait = null)
    {
        plugin ??= new VpnFleetPlugin(new FakeTimeProvider(), new FakeGitHub());
        plugin.ConnectTask.ConnectWait = wait ?? TimeSpan.FromSeconds(90);
        var ctx = new FakeTaskContext(camera) { Owner = plugin };
        await StepRun.RunAsync(ctx.Steps, () => plugin.ConnectTask.ExecuteAsync(ctx, device ?? new FakeDevice(), JsonSerializer.Serialize(payload, Fleet.Json), CancellationToken.None));
        return ctx;
    }

    private static async Task<(VpnFleetPlugin Plugin, FakeCoreContext Server)> StartAsync()
    {
        var server = new FakeCoreContext();
        var plugin = new VpnFleetPlugin(new FakeTimeProvider(), new FakeGitHub());
        await plugin.StartAsync(server, CancellationToken.None);
        return (plugin, server);
    }

    private static Task<string?> CallAsync<T>(FakeCoreContext server, VpnFleetPlugin plugin, string method, T request, ICallContext? user = null) =>
        server.InvokeAsync(plugin, user ?? server.CallAsAllDevices(), method, JsonSerializer.Serialize(request, Fleet.Json));

    private static async Task<FleetState> NextStateAsync(FakeCoreContext server, Func<FleetState, bool> match)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var item in server.Published.Reader.ReadAllAsync(timeout.Token))
        {
            var state = JsonSerializer.Deserialize<FleetState>(item.PayloadJson!, Fleet.Json)!;
            if (match(state))
            {
                return state;
            }
        }

        throw new TimeoutException();
    }
}
