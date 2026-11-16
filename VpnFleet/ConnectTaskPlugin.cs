using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace VpnFleet;

/// <summary>A camera's own WireGuard values: made on the server for this camera only.</summary>
public sealed record WireGuardCamera(string PrivateKey, string ClientIp);

/// <summary>
/// What one run of <see cref="ConnectTaskPlugin"/> does. Kept in memory only (it holds keys and passwords): the host
/// never stores a task payload.
/// </summary>
public sealed record ConnectPayload
{
    /// <summary>Set for the run that adds the cameras to a WireGuard Server hub; null for the cameras' run.</summary>
    public IReadOnlyList<HubPeer>? HubPeers { get; init; }

    public ConnectRequest Settings { get; init; } = new();

    /// <summary>WireGuard: the values of each camera of the run, by device id.</summary>
    public IReadOnlyDictionary<Guid, WireGuardCamera>? Cameras { get; init; }

    /// <summary>WireGuard: the server, its key and the networks reached through it, the same for every camera.</summary>
    public string? Endpoint { get; init; }

    public string? PeerPublicKey { get; init; }

    public string? AllowedIps { get; init; }
}

/// <summary>
/// The task the page starts, one per device: saves the VPN settings in the camera's VPN app, starts the app and waits
/// for the tunnel. The hub run adds the cameras to the peers of a WireGuard Server camera. Started only from the page.
/// </summary>
public sealed class ConnectTaskPlugin : ITaskPlugin
{
    public const string PluginId = Fleet.PluginId + ".connect";

    public const string CheckStep = "Check the device";
    public const string ProfileStep = "Upload the VPN profile";
    public const string SaveStep = "Save the VPN settings";
    public const string StartStep = "Start the app";
    public const string WaitStep = "Wait for the VPN to connect";
    public const string ReadPeersStep = "Read the peers of the WireGuard server";
    public const string AddPeersStep = "Add the cameras as peers";

    private readonly TimeProvider _time;

    internal ConnectTaskPlugin(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>How long a task waits for the tunnel before it ends with a warning.</summary>
    internal TimeSpan ConnectWait { get; set; } = TimeSpan.FromSeconds(90);

    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public string Id => PluginId;

    public string DisplayName => "Connect to a VPN";

    public string? IconKey => "network";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool ShowInMenus => false;

    public string GetTaskName(string? payloadJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<ConnectPayload>(payloadJson ?? "{}", Fleet.Json);
            if (payload?.HubPeers is { } peers)
            {
                return peers.Count == 1 ? "Add 1 camera to the WireGuard server" : $"Add {peers.Count} cameras to the WireGuard server";
            }

            return payload is null ? DisplayName : "Connect to " + VpnApps.NameOf(payload.Settings.Kind);
        }
        catch (JsonException)
        {
            return DisplayName;
        }
    }

    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return DeviceMessages.Refusal(device) is null;
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = JsonSerializer.Deserialize<ConnectPayload>(payloadJson ?? "null", Fleet.Json)
            ?? throw new ArgumentException("The task has no settings. Nothing was changed.");
        if (payload.HubPeers is { } peers)
        {
            await AddHubPeersAsync(ctx, peers, ct).ConfigureAwait(false);
            return;
        }

        await ConnectCameraAsync(ctx, device, payload, ct).ConfigureAwait(false);
    }

    private async Task ConnectCameraAsync(ITaskExecutionContext ctx, IDeviceInfo device, ConnectPayload payload, CancellationToken ct)
    {
        var settings = payload.Settings;
        var app = VpnApps.Of(settings.Kind);
        var problems = ConnectRules.Validate(settings);
        if (problems.Count > 0)
        {
            throw new ArgumentException(problems[0].Message + " Nothing was changed.");
        }

        WireGuardCamera? camera = null;
        if (settings.Kind == VpnKind.WireGuard && (payload.Cameras is null || !payload.Cameras.TryGetValue(device.Id, out camera)))
        {
            throw new ArgumentException("This camera has no WireGuard key in the task. Start the task from the VPN Fleet page again. Nothing was changed.");
        }

        var steps = new List<string> { CheckStep };
        if (settings.Kind == VpnKind.OpenVpn)
        {
            steps.Add(ProfileStep);
        }

        steps.AddRange([SaveStep, StartStep, WaitStep]);
        ctx.PlanSteps([.. steps]);

        var installed = await ctx.StepAsync(CheckStep, async step =>
        {
            (await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false)).Require("param-cgi", "1.0");
            var apps = await DeviceVpn.ListAppsAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var found = apps.FirstOrDefault(a => string.Equals(a.Name, app.AppName, StringComparison.OrdinalIgnoreCase))
                ?? throw new VpnDeviceException($"The {app.DisplayName} app is not installed on this device. Install it on the Applications page first. Nothing was changed.");
            step.Complete(app.DisplayName + (found.Version is { } v ? " " + v : ""));
            return found;
        }).ConfigureAwait(false);

        if (settings.Kind == VpnKind.OpenVpn)
        {
            await ctx.StepAsync(ProfileStep, _ => DeviceVpn.UploadOpenVpnProfileAsync(ctx.Vapix, settings.Profile!, ct)).ConfigureAwait(false);
        }

        await ctx.StepAsync(SaveStep, _ => DeviceVpn.SetParametersAsync(ctx.Vapix, ParametersOf(app, payload, camera), ct)).ConfigureAwait(false);
        ctx.Log(TaskLogLevel.Info, $"Saved the {app.DisplayName} settings" + (camera is null ? "." : $", tunnel address {camera.ClientIp}."));

        if (installed.IsRunning)
        {
            ctx.SkipStep(StartStep, "The app runs already.");
        }
        else
        {
            await ctx.StepAsync(StartStep, _ => DeviceVpn.StartAppAsync(ctx.Vapix, app.AppName, ct)).ConfigureAwait(false);
        }

        await WaitForTunnelAsync(ctx, app, ct).ConfigureAwait(false);
    }

    /// <summary>The app's parameters for param.cgi. Optional fields left empty keep what the app has.</summary>
    public static IReadOnlyDictionary<string, string> ParametersOf(VpnApp app, ConnectPayload payload, WireGuardCamera? camera)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(payload);
        var s = payload.Settings;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        void Set(string name, string? value, bool optional = false)
        {
            if (!optional || !string.IsNullOrWhiteSpace(value))
            {
                values[app.Parameter(name)] = value?.Trim() ?? "";
            }
        }

        switch (s.Kind)
        {
            case VpnKind.Tailscale:
                Set("AuthKey", s.AuthKey);
                Set("CustomServer", s.LoginServer, optional: true);
                break;
            case VpnKind.NetBird:
                Set("SetupKey", s.SetupKey);
                Set("ManagementURL", s.ManagementUrl, optional: true);
                break;
            case VpnKind.ZeroTier:
                Set("NetworkID", s.NetworkId?.Trim().ToLowerInvariant());
                break;
            case VpnKind.OpenVpn:
                Set("Username", s.UserName, optional: true);
                if (!string.IsNullOrEmpty(s.Password))
                {
                    values[app.Parameter("Password")] = s.Password;
                }

                break;
            case VpnKind.WireGuard:
                ArgumentNullException.ThrowIfNull(camera);
                Set("PrivateKey", camera.PrivateKey);
                Set("ClientIP", camera.ClientIp);
                Set("Endpoint", payload.Endpoint);
                Set("PeerPublicKey", payload.PeerPublicKey);
                Set("AllowedIPs", payload.AllowedIps);
                break;
        }

        return values;
    }

    private async Task WaitForTunnelAsync(ITaskExecutionContext ctx, VpnApp app, CancellationToken ct)
    {
        using var step = ctx.BeginStep(WaitStep);
        var end = _time.GetUtcNow() + ConnectWait;
        VpnStatus status;
        while (true)
        {
            try
            {
                status = await DeviceVpn.ReadStatusAsync(ctx.Vapix, app, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or VpnDeviceException)
            {
                status = new VpnStatus(VpnState.Unknown, null, ex.Message);
            }

            if (status.State == VpnState.Connected)
            {
                step.Complete(status.Address is null ? "Connected" : "Connected, " + status.Address);
                ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} is connected" + (status.Address is null ? "." : $" with address {status.Address}."));
                return;
            }

            if (_time.GetUtcNow() >= end)
            {
                break;
            }

            await Task.Delay(PollInterval, _time, ct).ConfigureAwait(false);
        }

        step.Warn($"Not connected after {ConnectWait.TotalSeconds:0} s" + (status.Detail is { } d ? ": " + d : ".") + " Check the VPN Fleet page later.");
    }

    private static async Task AddHubPeersAsync(ITaskExecutionContext ctx, IReadOnlyList<HubPeer> peers, CancellationToken ct)
    {
        var app = VpnApps.WireGuardServerAppName;
        ctx.PlanSteps(ReadPeersStep, AddPeersStep);
        var parameters = await ctx.StepAsync(ReadPeersStep, async step =>
        {
            (await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false)).Require("param-cgi", "1.0");
            var list = await ctx.Vapix.ListParametersAsync([app], ct).ConfigureAwait(false);
            if (!list.ContainsKey(app + ".Peers"))
            {
                throw new VpnDeviceException("The WireGuard Server app is not installed on this device. Nothing was changed.");
            }

            step.Complete(WireGuardAddressing.ParsePeers(list[app + ".Peers"]).Count + " peers");
            return list;
        }).ConfigureAwait(false);

        await ctx.StepAsync(AddPeersStep, async step =>
        {
            var merged = WireGuardAddressing.MergePeers(parameters[app + ".Peers"], peers);
            await DeviceVpn.SetParametersAsync(ctx.Vapix, new Dictionary<string, string> { ["root." + app + ".Peers"] = merged }, ct).ConfigureAwait(false);
            step.Complete(peers.Count == 1 ? "1 camera" : peers.Count + " cameras");
        }).ConfigureAwait(false);

        foreach (var peer in peers)
        {
            ctx.Log(TaskLogLevel.Info, $"Peer {peer.Name}: {peer.Address}");
        }

        if (!string.Equals(parameters.GetValueOrDefault(app + ".Enabled"), "yes", StringComparison.OrdinalIgnoreCase))
        {
            ctx.ReportWarning("The WireGuard server is turned off in its app. The cameras connect once it is on.");
        }
    }
}
