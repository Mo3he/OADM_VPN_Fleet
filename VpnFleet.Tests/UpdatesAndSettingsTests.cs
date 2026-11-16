using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

using VpnFleet.Client;

namespace VpnFleet.Tests;

public sealed class UpdatesAndSettingsTests
{
    private static readonly string[] TailscaleAssets =
    [
        "signed_Tailscale_VPN_1_104_7_aarch64.eap",
        "signed_Tailscale_VPN_1_104_7_armv7hf.eap",
        "Tailscale_VPN_1_104_7_aarch64_root.eap",
        "Tailscale_VPN_1_104_7_armv7hf_acap3.eap",
    ];

    // ---- Versions and packages ----

    [Theory]
    [InlineData("1.104.7", "1.104.6", true)]
    [InlineData("v1.104.10", "1.104.9", true)]
    [InlineData("1.104.7", "1.104.7", false)]
    [InlineData("0.83.1", "0.83.1-dev", false)]
    [InlineData("2.0", "1.99.99", true)]
    [InlineData("1.0", "unknown", false)]
    public void Newer_versions(string candidate, string installed, bool newer) => Assert.Equal(newer, AppVersions.IsNewer(candidate, installed));

    [Theory]
    [InlineData("aarch64", "12.11.77", "signed_Tailscale_VPN_1_104_7_aarch64.eap")]
    [InlineData("armv7hf", "11.11.124", "signed_Tailscale_VPN_1_104_7_armv7hf.eap")]
    [InlineData("armv7hf", "10.9.4", "Tailscale_VPN_1_104_7_armv7hf_acap3.eap")]
    [InlineData("armv7hf", "9.80.3", "Tailscale_VPN_1_104_7_armv7hf_acap3.eap")]
    public void Each_device_gets_the_package_for_its_architecture_and_AXIS_OS(string arch, string os, string expected)
    {
        var release = Release(VpnApps.Tailscale, TailscaleAssets);
        Assert.Equal(expected, ReleaseCatalog.ChooseAsset(release, arch, os).Asset?.Name);
    }

    [Fact]
    public void A_device_the_release_has_no_package_for_gets_the_reason()
    {
        var release = Release(VpnApps.Tailscale, TailscaleAssets);
        Assert.Contains("needs AXIS OS 10.12", ReleaseCatalog.ChooseAsset(release, "aarch64", "10.9.4").Reason, StringComparison.Ordinal);
        Assert.Contains("no package for mips", ReleaseCatalog.ChooseAsset(release, "mips", "12.0.0").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_files_of_the_apps_own_repository_count()
    {
        const string json = """
            {"tag_name":"v1.16.17","assets":[
              {"name":"signed_ZeroTier_VPN_1_16_17_aarch64.eap","size":10,"browser_download_url":"https://github.com/Mo3he/Axis_Cam_ZeroTier/releases/download/v1.16.17/signed_ZeroTier_VPN_1_16_17_aarch64.eap"},
              {"name":"evil_aarch64.eap","size":10,"browser_download_url":"https://example.com/evil_aarch64.eap"},
              {"name":"notes.txt","size":10,"browser_download_url":"https://github.com/Mo3he/Axis_Cam_ZeroTier/releases/download/v1.16.17/notes.txt"}]}
            """;
        var release = ReleaseCatalog.ParseRelease(VpnApps.ZeroTier, json)!;

        Assert.Equal("1.16.17", release.Version);
        Assert.Equal("signed_ZeroTier_VPN_1_16_17_aarch64.eap", Assert.Single(release.Assets).Name);
    }

    [Fact]
    public async Task Releases_are_asked_once_in_30_minutes()
    {
        var (_, _, github, time) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        using var catalog = new ReleaseCatalog(github, time, () => Path.GetTempPath());

        Assert.Equal("1.104.7", (await catalog.GetAsync(VpnKind.Tailscale, false, CancellationToken.None)).Release!.Version);
        await catalog.GetAsync(VpnKind.Tailscale, false, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(31));
        await catalog.GetAsync(VpnKind.Tailscale, false, CancellationToken.None);

        Assert.Equal(2, github.Requests.Count);
        var missing = await catalog.GetAsync(VpnKind.NetBird, false, CancellationToken.None);
        Assert.Null(missing.Release);
        Assert.Equal("GitHub answered HTTP 404.", missing.Error);
    }

    [Fact]
    public async Task A_download_is_checked_against_its_digest_and_stays_on_GitHub()
    {
        var (_, _, github, time) = await TestPlugin.StartAsync();
        github.Release(VpnApps.ZeroTier, "v1.16.17", "signed_ZeroTier_VPN_1_16_17_aarch64.eap");
        var folder = Path.Combine(Path.GetTempPath(), "vpnfleet-tests", Guid.NewGuid().ToString("N"));
        using var catalog = new ReleaseCatalog(github, time, () => folder);
        var asset = (await catalog.GetAsync(VpnKind.ZeroTier, false, CancellationToken.None)).Release!.Assets[0];

        var path = await catalog.DownloadAsync(asset, null, CancellationToken.None);
        Assert.Equal(github.Files[asset.Name], await File.ReadAllBytesAsync(path));
        Assert.Contains(github.Requests, r => r.StartsWith("https://objects.githubusercontent.com/", StringComparison.Ordinal));
        var before = github.Requests.Count;
        await catalog.DownloadAsync(asset, null, CancellationToken.None);
        Assert.Equal(before, github.Requests.Count); // kept, not downloaded again

        File.Delete(path);
        github.FileHost = "https://evil.example.com";
        await Assert.ThrowsAsync<HttpRequestException>(() => catalog.DownloadAsync(asset, null, CancellationToken.None));

        github.FileHost = "https://objects.githubusercontent.com";
        var tampered = asset with { Sha256 = new string('0', 64) };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.DownloadAsync(tampered, null, CancellationToken.None));
        Assert.Contains("does not match", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    // ---- Install and upgrade ----

    [Fact]
    public async Task Install_downloads_the_package_installs_starts_and_checks_the_version()
    {
        var (plugin, _, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        var camera = new AppCamera().On("armv7hf", "12.11.77");
        camera.Installs = ("Tailscale_VPN", "1.104.7");

        var ctx = await RunInstallAsync(plugin, camera, new InstallPayload(VpnKind.Tailscale, "1.104.7", false));

        var upload = Assert.Single(camera.Uploads);
        Assert.Contains("name=\"file\"; filename=\"signed_Tailscale_VPN_1_104_7_armv7hf.eap\"", upload, StringComparison.Ordinal);
        Assert.Contains("EAP signed_Tailscale_VPN_1_104_7_armv7hf.eap", upload, StringComparison.Ordinal);
        Assert.Equal(("Tailscale_VPN", "1.104.7", "Running"), Assert.Single(camera.Apps));
        Assert.All(StepRun.Lines(ctx.Steps), l => Assert.EndsWith(": Done", l, StringComparison.Ordinal));
        Assert.Empty(ctx.Warnings);
        Assert.Equal("Install Tailscale 1.104.7", plugin.InstallTask.GetTaskName(JsonSerializer.Serialize(new InstallPayload(VpnKind.Tailscale, "1.104.7", false), Fleet.Json)));
    }

    [Fact]
    public async Task A_device_with_the_newest_version_is_not_changed()
    {
        var (plugin, _, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        var camera = new AppCamera().WithApp("Tailscale_VPN", version: "1.104.7");

        var ctx = await RunInstallAsync(plugin, camera, new InstallPayload(VpnKind.Tailscale, "1.104.7", true));

        Assert.Empty(camera.Uploads);
        Assert.Contains($"{InstallTaskPlugin.InstallStep}: Skipped", StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task A_refused_package_fails_with_the_devices_reason()
    {
        var (plugin, _, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        var camera = new AppCamera().WithApp("Tailscale_VPN", version: "1.104.6");
        camera.UploadAnswer = "Error: 2";

        var ex = await Assert.ThrowsAsync<VpnDeviceException>(() => RunInstallAsync(plugin, camera, new InstallPayload(VpnKind.Tailscale, "1.104.7", true)));

        Assert.Equal("The device accepts only signed apps and this package's signature is not valid. (error 2)", ex.Message);
    }

    [Fact]
    public async Task Refresh_shows_updates_and_install_starts_one_task_per_device()
    {
        var (plugin, server, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        var old = server.Add(new FakeDevice { Address = "10.0.0.48" }, new AppCamera().WithApp("Tailscale_VPN", status: "Stopped", version: "1.104.6"));
        var none = server.Add(new FakeDevice { Address = "10.0.0.49" }, new AppCamera());

        await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.Refresh, JsonSerializer.Serialize(new RefreshRequest(true, []), Fleet.Json));
        var state = await NextStateAsync(server, s => !s.Reading && s.Rows.Count > 0);
        var row = state.Rows.Single(r => r.DeviceId == old.Id);
        Assert.True(row.UpdateAvailable);
        Assert.Equal("1.104.7", row.LatestVersion);
        Assert.EndsWith(". 1 update", VpnFleetViewModel.SummaryOf(state.Rows, false), StringComparison.Ordinal);

        var reply = JsonSerializer.Deserialize<InstallReply>((await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.Install,
            JsonSerializer.Serialize(new InstallRequest(VpnKind.Tailscale, [old.Id, none.Id], false), Fleet.Json)))!, Fleet.Json)!;

        var run = Assert.Single(server.TaskRuns);
        Assert.Equal(InstallTaskPlugin.PluginId, run.PluginId);
        Assert.Equal([old.Id, none.Id], run.DeviceIds);
        Assert.Equal("1.104.7", reply.Version);
    }

    // ---- Settings of one camera ----

    [Fact]
    public async Task Settings_come_with_labels_and_without_the_values_of_secrets()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var keys = WireGuardKeys.Generate();
        var camera = new AppCamera().WithApp("wireguardconfig");
        camera.Parameters["wireguardconfig.PrivateKey"] = keys.PrivateKey;
        camera.Parameters["wireguardconfig.ClientIP"] = "10.66.0.2/24";
        camera.Parameters["wireguardconfig.NewSetting"] = "x";
        var device = server.Add(new FakeDevice { Address = "10.0.0.48" }, camera);

        var reply = await GetSettingsAsync(plugin, server, device.Id, VpnKind.WireGuard);

        Assert.Equal(["PrivateKey", "ClientIP", "NewSetting"], reply.Fields.Select(f => f.Name));
        var secret = reply.Fields[0];
        Assert.Equal((SettingKind.Secret, "", true), (secret.Kind, secret.Value, secret.HasValue));
        Assert.Equal(("Tunnel address", "10.66.0.2/24"), (reply.Fields[1].Label, reply.Fields[1].Value));
        Assert.Equal(keys.PublicKey, reply.PublicKey);
        Assert.DoesNotContain(reply.Fields, f => f.Value.Contains(keys.PrivateKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_writes_only_the_changes_of_this_camera_and_keeps_an_empty_secret()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var camera = new AppCamera().WithApp("Tailscale_VPN");
        camera.Parameters["Tailscale_VPN.AuthKey"] = "tskey-old";
        camera.Parameters["Tailscale_VPN.AcceptDNS"] = "false";
        camera.Parameters["Tailscale_VPN.ExitNode"] = "";
        var device = server.Add(new FakeDevice { Address = "10.0.0.48" }, camera);
        var other = server.Add(new FakeDevice { Address = "10.0.0.49" }, new AppCamera().WithApp("Tailscale_VPN"));

        var reply = await SaveAsync(plugin, server, new SaveSettingsRequest(device.Id, VpnKind.Tailscale, new Dictionary<string, string> { ["AcceptDNS"] = "true", ["AuthKey"] = "" }));

        Assert.Equal(1, reply.Changed);
        Assert.Equal("true", camera.Parameters["root.Tailscale_VPN.AcceptDNS"]);
        Assert.False(camera.Parameters.ContainsKey("root.Tailscale_VPN.AuthKey"));
        Assert.Equal(0, ((AppCamera)server.Cameras[other.Id]).Writes);
    }

    [Theory]
    [InlineData("Unknown", "x", "This app has no setting Unknown.")]
    [InlineData("AcceptDNS", "maybe", "Choose one of the options.")]
    [InlineData("ExitNode", "a\nb", "Enter one line.")]
    [InlineData("HttpProxyPort", "70000", "Enter a port from 1 to 65535.")]
    public async Task Save_refuses_a_bad_change_before_writing(string name, string value, string message)
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var camera = new AppCamera().WithApp("Tailscale_VPN");
        camera.Parameters["Tailscale_VPN.AcceptDNS"] = "false";
        camera.Parameters["Tailscale_VPN.ExitNode"] = "";
        camera.Parameters["Tailscale_VPN.HttpProxyPort"] = "8080";
        var device = server.Add(new FakeDevice { Address = "10.0.0.48" }, camera);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            SaveAsync(plugin, server, new SaveSettingsRequest(device.Id, VpnKind.Tailscale, new Dictionary<string, string> { [name] = value })));

        Assert.Equal(message, ex.Message);
        Assert.Equal(0, camera.Writes);
    }

    [Fact]
    public async Task A_new_key_of_a_camera_behind_a_hub_goes_to_the_hub()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var hub = server.Add(new FakeDevice { Address = "10.0.0.10" }, new AppCamera());
        var camera = new AppCamera().WithApp("wireguardconfig");
        camera.Parameters["wireguardconfig.PrivateKey"] = WireGuardKeys.Generate().PrivateKey;
        var device = server.Add(new FakeDevice { Address = "10.0.0.48", Serial = "ACCC8E000001" }, camera);
        var record = new WireGuardRecord(device.Id, "ACCC8E000001", "old", "10.66.0.3", "10.66.0.0/24", hub.Id);
        server.PluginSettings.Values[VpnFleetPlugin.WireGuardSettingKey] = JsonSerializer.Serialize(new[] { record }, Fleet.Json);
        var keys = WireGuardKeys.Generate();

        var reply = await SaveAsync(plugin, server, new SaveSettingsRequest(device.Id, VpnKind.WireGuard, new Dictionary<string, string> { ["PrivateKey"] = keys.PrivateKey }));

        Assert.Equal(keys.PrivateKey, camera.Parameters["root.wireguardconfig.PrivateKey"]);
        var run = Assert.Single(server.TaskRuns);
        Assert.Equal([hub.Id], run.DeviceIds);
        var peer = Assert.Single(JsonSerializer.Deserialize<ConnectPayload>(run.PayloadJson!, Fleet.Json)!.HubPeers!);
        Assert.Equal(new HubPeer("ACCC8E000001", keys.PublicKey, "10.66.0.3"), peer);
        Assert.Contains("hub camera gets the new public key", reply.Note, StringComparison.Ordinal);
        var stored = JsonSerializer.Deserialize<List<WireGuardRecord>>(server.PluginSettings.Values[VpnFleetPlugin.WireGuardSettingKey]!, Fleet.Json)!;
        Assert.Equal(keys.PublicKey, Assert.Single(stored).PublicKey);
    }

    // ---- The page ----

    [Fact]
    public void A_setting_is_changed_only_when_the_user_changed_it()
    {
        var text = new SettingFieldViewModel(VpnApps.ZeroTier, new SettingValue("NetworkID", "Network ID", SettingKind.Text, "8056c2e21c000001", true));
        var secret = new SettingFieldViewModel(VpnApps.Tailscale, new SettingValue("AuthKey", "Auth key", SettingKind.Secret, "", true));
        var flag = new SettingFieldViewModel(VpnApps.Tailscale, new SettingValue("AcceptDNS", "DNS", SettingKind.Bool, "false", true) { Options = [new("true", "On"), new("false", "Off")] });

        Assert.False(text.IsDirty || secret.IsDirty || flag.IsDirty);
        Assert.Equal("Set. Enter a new one to replace it.", secret.Placeholder);

        text.Value = "123";
        Assert.Equal("Enter the 16-character network ID.", text.Error);
        flag.BoolValue = true;
        Assert.Equal("true", flag.Value);
        Assert.True(flag.IsDirty);
    }

    [Fact]
    public async Task The_checklists_say_what_happens_to_each_camera()
    {
        var (plugin, server, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        var withApp = server.Add(new FakeDevice { Address = "10.0.0.48" }, new AppCamera().WithApp("Tailscale_VPN", version: "1.104.6"));
        var without = server.Add(new FakeDevice { Address = "10.0.0.49" }, new AppCamera());
        server.Add(new FakeDevice { Address = "10.0.0.50" }, new AppCamera()); // not read
        var ctx = new FakePageContext(plugin, server) { SelectedDevices = [.. server.DeviceList] };
        using var page = new VpnFleetViewModel(ctx);
        await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.Refresh, JsonSerializer.Serialize(new RefreshRequest(false, [withApp.Id, without.Id]), Fleet.Json));
        page.Apply(JsonSerializer.Serialize(await NextStateAsync(server, s => !s.Reading && s.Rows.Count > 0), Fleet.Json));

        var connect = page.ConnectChecklist(VpnKind.Tailscale, null);
        Assert.Equal(
            ["10.0.0.48 True Tailscale installed", "10.0.0.49 False Tailscale not installed. Install it first.", "10.0.0.50 True Not read yet. Refresh to check it has the app."],
            connect.Select(c => $"{c.Label.Split(' ')[0]} {c.IsChecked} {c.Note}"));

        var install = page.InstallChecklist(VpnKind.Tailscale, "1.104.7");
        Assert.Equal(
            ["10.0.0.48 True Upgrade 1.104.6 to 1.104.7", "10.0.0.49 False Install Tailscale 1.104.7", "10.0.0.50 False Not read yet"],
            install.Select(c => $"{c.Label.Split(' ')[0]} {c.IsChecked} {c.Note}"));

        var hub = page.ConnectChecklist(VpnKind.Tailscale, withApp.Id)[0];
        Assert.False(hub.IsChecked || hub.IsEnabled);
    }

    [Fact]
    public void The_checklist_counts_the_ticked_cameras()
    {
        var list = new CameraChecklist();
        list.Set([new CameraCheck(Guid.NewGuid(), "a", "", true), new CameraCheck(Guid.NewGuid(), "b", "", false), new CameraCheck(Guid.NewGuid(), "hub", "", true, isEnabled: false)]);
        Assert.Equal("1 of 3 cameras will be changed.", list.Summary);

        list.CheckAllCommand.Execute(null);
        Assert.Equal(2, list.CheckedCount); // never the disabled one
        list.Items[0].IsChecked = false;
        Assert.Single(list.CheckedIds);
    }

    // ---- Helpers ----

    private static AppRelease Release(VpnApp app, params string[] assets) =>
        new(app.Kind, "1.104.7", "https://github.com/", null, [.. assets.Select(a => new ReleaseAsset(a, 1, "https://github.com/x/" + a, null))]);

    private static async Task<FakeTaskContext> RunInstallAsync(VpnFleetPlugin plugin, AppCamera camera, InstallPayload payload)
    {
        var ctx = new FakeTaskContext(camera) { Owner = plugin };
        await StepRun.RunAsync(ctx.Steps, () => plugin.InstallTask.ExecuteAsync(ctx, new FakeDevice(), JsonSerializer.Serialize(payload, Fleet.Json), CancellationToken.None));
        return ctx;
    }

    private static async Task<SettingsReply> GetSettingsAsync(VpnFleetPlugin plugin, FakeCoreContext server, Guid deviceId, VpnKind kind) =>
        JsonSerializer.Deserialize<SettingsReply>((await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.GetSettings,
            JsonSerializer.Serialize(new SettingsRequest(deviceId, kind), Fleet.Json)))!, Fleet.Json)!;

    private static async Task<SaveSettingsReply> SaveAsync(VpnFleetPlugin plugin, FakeCoreContext server, SaveSettingsRequest request) =>
        JsonSerializer.Deserialize<SaveSettingsReply>((await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.SaveSettings,
            JsonSerializer.Serialize(request, Fleet.Json)))!, Fleet.Json)!;

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
