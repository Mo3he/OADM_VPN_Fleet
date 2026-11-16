using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace VpnFleet;

/// <summary>A camera's WireGuard tunnel as the plugin remembers it: never its private key.</summary>
public sealed record WireGuardRecord(Guid DeviceId, string PeerName, string PublicKey, string Address, string Network, Guid? HubDeviceId);

/// <summary>
/// The server part of VPN Fleet. The page reads the VPN apps of the chosen devices (a job per read, one table per
/// device group) and connects devices to a VPN (one task per device). For WireGuard it makes each camera's keys,
/// gives it a tunnel address and, with a WireGuard Server camera as hub, adds the cameras to the hub's peers.
/// </summary>
public sealed class VpnFleetPlugin : ICorePlugin
{
    public const string WireGuardSettingKey = "wireguard";

    private const int ReadParallelism = 16;
    private static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, FleetState> _states = []; // by caller.DeviceScopeKey
    private readonly SemaphoreSlim _wireGuardGate = new(1, 1);
    private readonly ConnectTaskPlugin _connectTask;
    private ICorePluginContext? _ctx;
    private int _version;

    public VpnFleetPlugin()
        : this(TimeProvider.System)
    {
    }

    internal VpnFleetPlugin(TimeProvider time)
    {
        _connectTask = new ConnectTaskPlugin(time);
    }

    internal ConnectTaskPlugin ConnectTask => _connectTask;

    public string Id => Fleet.PluginId;

    public string DisplayName => "VPN Fleet";

    public string? IconKey => "route";

    public CorePluginGroup Group => CorePluginGroup.Security;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [_connectTask];

    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(Fleet.ConnectPermission, "Connect devices to a VPN", "Save VPN keys and settings in the VPN apps of devices.") { OperatorDefault = false },
    ];

    public string? RequiredPermission(string method) => method == Fleet.Connect ? Fleet.ConnectPermission : PermissionKeys.Show;

    public bool IsAudited(string method) => method == Fleet.Connect;

    public string? AuditDetail(string method, string? payloadJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<ConnectRequest>(payloadJson ?? "null", Fleet.Json);
            return request is null
                ? null
                : $"Connect {(request.All ? "all" : request.DeviceIds.Count.ToString(CultureInfo.InvariantCulture))} devices to {VpnApps.NameOf(request.Kind)}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        if (ctx.DeviceScopes is { } scopes)
        {
            scopes.Changed += OnGroupChanged;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (_ctx?.DeviceScopes is { } scopes)
        {
            scopes.Changed -= OnGroupChanged;
        }

        return Task.CompletedTask;
    }

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("The page needs the user's call.");

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var scope = caller.DeviceScopeKey;
        switch (method)
        {
            case Fleet.GetState:
                return Serialize(InGroup(scope, StateOf(scope)));

            case Fleet.Refresh:
                var request = JsonSerializer.Deserialize<RefreshRequest>(payloadJson ?? "null", Fleet.Json);
                if (request is null || (!request.All && (request.DeviceIds is null || request.DeviceIds.Count == 0)))
                {
                    throw new ArgumentException(Fleet.NothingChosen);
                }

                var devices = request.All ? await caller.Devices.ListAsync(ct).ConfigureAwait(false) : await FindAsync(caller, request.DeviceIds, ct).ConfigureAwait(false);
                if (!TryBeginRead(scope))
                {
                    throw new InvalidOperationException("The devices are being read already.");
                }

                try
                {
                    _ctx!.Jobs!.Start(caller, "read", (job, token) => ReadAsync(job.Owner, scope, devices, token));
                }
                catch
                {
                    Publish(scope, StateOf(scope) with { Reading = false });
                    throw;
                }

                return null;

            case Fleet.Connect:
                var connect = JsonSerializer.Deserialize<ConnectRequest>(payloadJson ?? "null", Fleet.Json)
                    ?? throw new ArgumentException("The request has no settings.");
                return JsonSerializer.Serialize(await ConnectAsync(caller, connect, ct).ConfigureAwait(false), Fleet.Json);

            case Fleet.GetPeers:
                return JsonSerializer.Serialize(await PeersAsync(caller, ct).ConfigureAwait(false), Fleet.Json);

            default:
                throw new ArgumentException("Unknown method " + method);
        }
    }

    private static async Task<List<IDeviceInfo>> FindAsync(ICallContext caller, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var devices = new List<IDeviceInfo>(ids.Count);
        foreach (var id in ids.Distinct())
        {
            devices.Add(await caller.Devices.FindAsync(id, ct).ConfigureAwait(false) ?? throw new DevicesNotAvailableException());
        }

        return devices;
    }

    // ---- Connect ----

    private async Task<ConnectReply> ConnectAsync(ICallContext caller, ConnectRequest request, CancellationToken ct)
    {
        if (ConnectRules.Validate(request) is { Count: > 0 } problems)
        {
            throw new ArgumentException(problems[0].Message);
        }

        if (!request.All && request.DeviceIds.Count == 0)
        {
            throw new ArgumentException(Fleet.NothingChosen);
        }

        IReadOnlyList<IDeviceInfo> devices = request.All
            ? await caller.Devices.ListAsync(ct).ConfigureAwait(false)
            : await FindAsync(caller, request.DeviceIds, ct).ConfigureAwait(false);
        devices = [.. devices.Where(d => d.Id != request.HubDeviceId)];
        if (devices.Count == 0)
        {
            throw new ArgumentException("Choose devices other than the hub.");
        }

        var settings = request with { DeviceIds = [] };
        if (request.Kind != VpnKind.WireGuard)
        {
            var ids = await caller.Tasks.RunAsync(ConnectTaskPlugin.PluginId, [.. devices.Select(d => d.Id)],
                JsonSerializer.Serialize(new ConnectPayload { Settings = settings }, Fleet.Json), caller.UserName, ct).ConfigureAwait(false);
            return new ConnectReply(devices.Count, ids);
        }

        return await ConnectWireGuardAsync(caller, settings, devices, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes a key pair and a tunnel address for every camera, remembers them (public keys only), adds the cameras to the
    /// hub when there is one, then starts the cameras' tasks. One run at a time, so two runs never hand out one address.
    /// </summary>
    private async Task<ConnectReply> ConnectWireGuardAsync(ICallContext caller, ConnectRequest settings, IReadOnlyList<IDeviceInfo> devices, CancellationToken ct)
    {
        await _wireGuardGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadRecordsAsync(ct).ConfigureAwait(false);
            IDeviceInfo? hub = null;
            AddressPool pool;
            string endpoint, serverKey;
            var reserved = new HashSet<uint>();
            if (settings.HubDeviceId is { } hubId)
            {
                hub = await caller.Devices.FindAsync(hubId, ct).ConfigureAwait(false) ?? throw new DevicesNotAvailableException();
                if (DeviceMessages.Refusal(hub, refuseUnreachable: true) is { } refusal)
                {
                    throw new InvalidOperationException("The hub camera cannot be used: " + refusal);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(DeviceTimeout);
                var vapix = await caller.Vapix.CreateAsync(hub.Id, timeout.Token).ConfigureAwait(false);
                var hubInfo = await DeviceVpn.ReadHubAsync(vapix, hub.Address, timeout.Token).ConfigureAwait(false);
                pool = hubInfo.Pool;
                endpoint = hubInfo.Endpoint;
                serverKey = hubInfo.PublicKey;
                reserved.Add(pool.Address);

                // The hub's other peers keep their addresses; the cameras of this run get theirs back.
                var names = devices.Select(d => WireGuardAddressing.PeerName(d.Serial, d.Address)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var peer in WireGuardAddressing.ParsePeers(hubInfo.Peers).Where(p => !names.Contains(p.Name)))
                {
                    if (WireGuardAddressing.ParseAddress(peer.Address) is { } used)
                    {
                        reserved.Add(used);
                    }
                }
            }
            else
            {
                pool = WireGuardAddressing.ParsePool(settings.AddressPool)!;
                endpoint = settings.Endpoint!.Trim();
                serverKey = settings.ServerPublicKey!.Trim();
                reserved.Add(pool.IsHost(pool.Address) ? pool.Address : pool.Network + 1); // the server's own address
            }

            var runIds = devices.Select(d => d.Id).ToHashSet();
            foreach (var other in records.Where(r => !runIds.Contains(r.DeviceId) && r.Network == pool.Subnet && r.HubDeviceId == settings.HubDeviceId))
            {
                if (WireGuardAddressing.ParseAddress(other.Address) is { } used)
                {
                    reserved.Add(used);
                }
            }

            var previous = records
                .Where(r => runIds.Contains(r.DeviceId) && r.Network == pool.Subnet)
                .Select(r => (r.DeviceId, Address: WireGuardAddressing.ParseAddress(r.Address)))
                .Where(r => r.Address is not null)
                .ToDictionary(r => r.DeviceId, r => r.Address!.Value);
            var addresses = WireGuardAddressing.Allocate(pool, [.. devices.Select(d => d.Id)], previous, reserved);

            var cameras = new Dictionary<Guid, WireGuardCamera>();
            var peers = new List<HubPeer>();
            var updated = records.Where(r => !runIds.Contains(r.DeviceId)).ToList();
            foreach (var device in devices)
            {
                var keys = WireGuardKeys.Generate();
                var address = WireGuardAddressing.Format(addresses[device.Id]);
                var name = WireGuardAddressing.PeerName(device.Serial, device.Address);
                cameras[device.Id] = new WireGuardCamera(keys.PrivateKey, pool.WithPrefix(addresses[device.Id]));
                peers.Add(new HubPeer(name, keys.PublicKey, address));
                updated.Add(new WireGuardRecord(device.Id, name, keys.PublicKey, address, pool.Subnet, settings.HubDeviceId));
            }

            var allowed = WireGuardAddressing.ParseNetworks(settings.AllowedIps) is { Count: > 0 } networks ? string.Join(",", networks) : pool.Subnet;
            var taskIds = new List<Guid>();
            if (hub is not null)
            {
                taskIds.AddRange(await caller.Tasks.RunAsync(ConnectTaskPlugin.PluginId, [hub.Id],
                    JsonSerializer.Serialize(new ConnectPayload { Settings = settings, HubPeers = peers }, Fleet.Json), caller.UserName, ct).ConfigureAwait(false));
            }

            await _ctx!.Settings.SetAsync(WireGuardSettingKey, JsonSerializer.Serialize(updated, Fleet.Json), ct).ConfigureAwait(false);
            var payload = new ConnectPayload
            {
                Settings = settings,
                Cameras = cameras,
                Endpoint = endpoint,
                PeerPublicKey = serverKey,
                AllowedIps = allowed,
            };
            taskIds.AddRange(await caller.Tasks.RunAsync(ConnectTaskPlugin.PluginId, [.. devices.Select(d => d.Id)],
                JsonSerializer.Serialize(payload, Fleet.Json), caller.UserName, ct).ConfigureAwait(false));
            return new ConnectReply(devices.Count, taskIds);
        }
        finally
        {
            _wireGuardGate.Release();
        }
    }

    /// <summary>The [Peer] sections of the user's cameras that use another WireGuard server than a hub camera.</summary>
    private async Task<PeersReply> PeersAsync(ICallContext caller, CancellationToken ct)
    {
        var sections = new List<string>();
        foreach (var record in (await LoadRecordsAsync(ct).ConfigureAwait(false)).Where(r => r.HubDeviceId is null))
        {
            if (await caller.Devices.FindAsync(record.DeviceId, ct).ConfigureAwait(false) is { } device)
            {
                sections.Add(WireGuardAddressing.PeerSection($"{device.Address} {device.Model} ({record.PeerName})", record.PublicKey, record.Address));
            }
        }

        return new PeersReply(sections.Count, string.Join("\n", sections));
    }

    private async Task<List<WireGuardRecord>> LoadRecordsAsync(CancellationToken ct)
    {
        var json = await _ctx!.Settings.GetAsync(WireGuardSettingKey, ct).ConfigureAwait(false);
        return json is null ? [] : JsonSerializer.Deserialize<List<WireGuardRecord>>(json, Fleet.Json) ?? [];
    }

    // ---- Read ----

    private async Task ReadAsync(ICallContext owner, string scope, IReadOnlyList<IDeviceInfo> devices, CancellationToken ct)
    {
        var rows = new ConcurrentBag<FleetRow>();
        try
        {
            await Parallel.ForEachAsync(devices, new ParallelOptions { MaxDegreeOfParallelism = ReadParallelism, CancellationToken = ct }, async (device, token) =>
            {
                if (InGroup(scope, device.Id))
                {
                    foreach (var row in await ReadDeviceAsync(owner, device, token).ConfigureAwait(false))
                    {
                        rows.Add(row);
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Publish(scope, StateOf(scope) with { Reading = false });
            return;
        }

        Publish(scope, StateOf(scope) with
        {
            Reading = false,
            Rows = [.. rows.OrderBy(r => r.Address, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Vpn)],
        });
    }

    /// <summary>One row per VPN app on the device, or one row that says there is none or why it was not read.</summary>
    internal static async Task<IReadOnlyList<FleetRow>> ReadDeviceAsync(ICallContext owner, IDeviceInfo device, CancellationToken ct)
    {
        var model = device.Model ?? "";
        FleetRow Problem(string error) => new(device.Id, device.Address, model, null, null, VpnState.Unknown, null, null, error);
        if (DeviceMessages.Refusal(device, refuseUnreachable: true) is { } refusal)
        {
            return [Problem(refusal)];
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DeviceTimeout);
        try
        {
            var vapix = await owner.Vapix.CreateAsync(device.Id, timeout.Token).ConfigureAwait(false);
            var apps = await DeviceVpn.ListAppsAsync(vapix, timeout.Token).ConfigureAwait(false);
            var rows = new List<FleetRow>();
            foreach (var installed in apps)
            {
                if (VpnApps.ByAppName(installed.Name) is not { } app)
                {
                    continue;
                }

                var status = installed.IsRunning
                    ? await DeviceVpn.ReadStatusAsync(vapix, app, timeout.Token).ConfigureAwait(false)
                    : new VpnStatus(VpnState.AppStopped, null, null);
                rows.Add(new FleetRow(device.Id, device.Address, model, app.Kind, installed.Version, status.State, status.Address, status.Detail, null));
            }

            return rows.Count > 0 ? rows : [new FleetRow(device.Id, device.Address, model, null, null, VpnState.NoVpnApp, null, null, null)];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return [Problem(DeviceMessages.TimedOut)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [Problem(ex.Message)];
        }
    }

    // ---- State per device group ----

    private bool InGroup(string scope, Guid deviceId) => _ctx!.DeviceScopes?.Contains(scope, deviceId) ?? true;

    private FleetState InGroup(string scope, FleetState state) =>
        state with { Rows = [.. state.Rows.Where(r => InGroup(scope, r.DeviceId))] };

    private void OnGroupChanged(object? sender, string scope) => Publish(scope, StateOf(scope) with { Rows = [] });

    private FleetState StateOf(string scope)
    {
        lock (_gate)
        {
            return _states.GetValueOrDefault(scope) ?? new FleetState(_version, false, []);
        }
    }

    private bool TryBeginRead(string scope)
    {
        lock (_gate)
        {
            var state = StateOf(scope);
            if (state.Reading)
            {
                return false;
            }

            Publish(scope, state with { Reading = true });
            return true;
        }
    }

    private void Publish(string scope, FleetState state)
    {
        string json;
        lock (_gate)
        {
            state = InGroup(scope, state) with { Version = ++_version };
            _states[scope] = state;
            json = Serialize(state);
        }

        _ctx?.Events?.Publish(Fleet.StateTopic, json, PluginEventAudience.DeviceScope(scope));
    }

    private static string Serialize(FleetState state) => JsonSerializer.Serialize(state, Fleet.Json);
}
