using System.ComponentModel;
using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace VpnFleet.Client;

/// <summary>The panel right of the table.</summary>
public enum FleetPanel
{
    None,
    Camera,
    Connect,
    Install,
    Peers,
}

/// <summary>
/// The page: the VPN apps of the chosen devices, their tunnels and versions. A row opens its camera's settings; the
/// toolbar sets up or installs on many cameras, each one ticked in a list. Reads the state from the server part, then
/// follows its events. Nothing is read before a device choice.
/// </summary>
public sealed partial class VpnFleetViewModel : ObservableObject, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private int _version = -1;
    private Dictionary<Guid, List<FleetRow>> _rowsByDevice = [];
    private VpnKind? _connectKind;

    public VpnFleetViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx;
        Devices = new PageDeviceChoice(ctx);
        Devices.PropertyChanged += (_, _) => NotifyCommands();
        MayConnect = ctx.Has(PermissionKeys.Of(Fleet.PluginId, Fleet.ConnectPermission));
        MayInstall = ctx.Has(PermissionKeys.Of(Fleet.PluginId, Fleet.InstallPermission));
        Form = new ConnectFormViewModel { NameOf = id => _ctx.Devices.FirstOrDefault(d => d.Id == id) is { } d ? DeviceLabel(d) : null };
        Form.PropertyChanged += OnFormChanged;
        ConnectCameras.Changed += (_, _) => NotifyCommands();
        Install = new InstallViewModel(ctx, _stop.Token)
        {
            BuildChecklist = InstallChecklist,
            StartInstall = StartInstallAsync,
        };
    }

    public PageDeviceChoice Devices { get; }

    public ConnectFormViewModel Form { get; }

    /// <summary>The cameras "Set up many cameras" changes.</summary>
    public CameraChecklist ConnectCameras { get; } = new();

    public InstallViewModel Install { get; }

    /// <summary>Opens the device picker; set by the view, which knows the window.</summary>
    public Func<Task<bool>>? PickDevices { get; set; }

    /// <summary>Opens a web page; set by the view.</summary>
    public Action<string>? OpenUrl
    {
        get;
        set
        {
            field = value;
            Install.OpenUrl = value;
        }
    }

    /// <summary>Opens a file dialog for an OpenVPN profile; set by the view.</summary>
    public Func<Task<(string Name, string Text)?>>? PickProfile { get; set; }

    public RangeObservableCollection<FleetRowView> Rows { get; } = [];

    public bool MayConnect { get; }

    public bool MayInstall { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsReading { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial bool IsConnecting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCameraOpen), nameof(IsConnectOpen), nameof(IsInstallOpen), nameof(IsPeersOpen))]
    public partial FleetPanel Panel { get; set; }

    public bool IsCameraOpen => Panel == FleetPanel.Camera;

    public bool IsConnectOpen => Panel == FleetPanel.Connect;

    public bool IsInstallOpen => Panel == FleetPanel.Install;

    public bool IsPeersOpen => Panel == FleetPanel.Peers;

    /// <summary>The selected row of the table; a row opens its camera's panel.</summary>
    [ObservableProperty]
    public partial FleetRowView? SelectedRow { get; set; }

    [ObservableProperty]
    public partial CameraSettingsViewModel? Camera { get; private set; }

    [ObservableProperty]
    public partial string PeersText { get; private set; } = "";

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    public partial string? InfoText { get; private set; }

    /// <summary>"12 devices: 9 connected, 2 not connected, 1 without a VPN app. 3 updates".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    /// <summary>Why the newest versions are not known; null when they are.</summary>
    [ObservableProperty]
    public partial string? ReleaseNote { get; private set; }

    public string? ConnectBlockedReason => Form.FormError ?? (ConnectCameras.CheckedCount == 0 ? "Tick the cameras first." : null);

    public string ConnectText => ConnectCameras.CheckedCount == 1 ? "Set up 1 camera" : $"Set up {ConnectCameras.CheckedCount:N0} cameras";

    public void Start() => _run ??= RunAsync();

    private Task RunAsync() => _ctx.RunAsync(
        async ct => Apply(await _ctx.InvokeAsync(Fleet.GetState, null, ct)),
        item =>
        {
            if (item.Topic == Fleet.StateTopic)
            {
                Apply(item.PayloadJson);
            }
        },
        new PluginEventWatchOptions { Failed = ex => ErrorText = ServerErrors.Message(ex) },
        _stop.Token);

    [RelayCommand]
    private async Task ChooseDevicesAsync()
    {
        if (PickDevices is { } pick && await pick() && CanRefresh())
        {
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        ErrorText = null;
        var all = Devices.Choice.Kind == DeviceChoiceKind.All;
        var ids = all ? [] : Devices.Resolve().Select(d => d.Id).ToList();
        ErrorText = await GuardedRun.RunAsync(
            () => _ctx.InvokeAsync(Fleet.Refresh, JsonSerializer.Serialize(new RefreshRequest(all, ids), Fleet.Json), _stop.Token),
            GuardedCancel.Ignore);
    }

    private bool CanRefresh() => !IsReading && !Devices.IsEmpty;

    // ---- Panels ----

    partial void OnSelectedRowChanged(FleetRowView? value)
    {
        if (value is null)
        {
            return; // the table was reloaded: the open panel stays
        }

        Camera = new CameraSettingsViewModel(_ctx, value, MayConnect, MayInstall, _stop.Token)
        {
            OpenUrl = OpenUrl,
            PickProfile = PickProfile,
            StartInstall = StartInstallAsync,
        };
        Panel = FleetPanel.Camera;
        _ = Camera.LoadAsync();
    }

    [RelayCommand]
    private void ClosePanel() => Panel = FleetPanel.None;

    [RelayCommand]
    private void OpenConnect()
    {
        Panel = Panel == FleetPanel.Connect ? FleetPanel.None : FleetPanel.Connect;
        if (Panel == FleetPanel.Connect)
        {
            RebuildConnectCameras(keepTicks: false);
        }
    }

    [RelayCommand]
    private async Task OpenInstallAsync()
    {
        Panel = Panel == FleetPanel.Install ? FleetPanel.None : FleetPanel.Install;
        if (Panel == FleetPanel.Install)
        {
            await Install.LoadReleasesAsync(checkNow: false);
        }
    }

    [RelayCommand]
    private async Task ShowPeersAsync()
    {
        Panel = Panel == FleetPanel.Peers ? FleetPanel.None : FleetPanel.Peers;
        if (Panel != FleetPanel.Peers)
        {
            return;
        }

        string? reply = null;
        ErrorText = await GuardedRun.RunAsync(async () => reply = await _ctx.InvokeAsync(Fleet.GetPeers, null, _stop.Token), GuardedCancel.Ignore);
        if (reply is not null && JsonSerializer.Deserialize<PeersReply>(reply, Fleet.Json) is { } peers)
        {
            PeersText = peers.Count == 0
                ? "No camera uses another WireGuard server yet. Set up cameras with WireGuard and \"Another WireGuard server\" first."
                : peers.Text;
        }
    }

    // ---- Set up many cameras ----

    /// <summary>
    /// The chosen devices with what "Set up" does to each: the ones with the app are ticked, the ones without it are not
    /// (the task would refuse them), the hub is never in.
    /// </summary>
    public IReadOnlyList<CameraCheck> ConnectChecklist(VpnKind kind, Guid? hubId)
    {
        var app = VpnApps.Of(kind);
        var list = new List<CameraCheck>();
        foreach (var device in Devices.Resolve())
        {
            var label = DeviceLabel(device);
            if (device.Id == hubId)
            {
                list.Add(new CameraCheck(device.Id, label, "Hub camera: gets the others as peers.", false, isEnabled: false));
            }
            else if (!_rowsByDevice.TryGetValue(device.Id, out var rows))
            {
                list.Add(new CameraCheck(device.Id, label, "Not read yet. Refresh to check it has the app.", true));
            }
            else if (rows.Any(r => r.Vpn == kind))
            {
                list.Add(new CameraCheck(device.Id, label, $"{app.DisplayName} installed", true) { HasApp = true });
            }
            else if (rows.FirstOrDefault(r => r.Error is not null) is { } failed)
            {
                list.Add(new CameraCheck(device.Id, label, "Not read: " + failed.Error, false, isWarning: true));
            }
            else
            {
                list.Add(new CameraCheck(device.Id, label, $"{app.DisplayName} not installed. Install it first.", false, isWarning: true));
            }
        }

        return list;
    }

    private void RebuildConnectCameras(bool keepTicks)
    {
        var before = keepTicks ? ConnectCameras.Items.ToDictionary(i => i.Id, i => i.IsChecked) : [];
        var hub = Form.IsWireGuardHub ? Form.HubDeviceId : null;
        var items = ConnectChecklist(Form.Kind, hub)
            .Select(i => before.TryGetValue(i.Id, out var was) && i.IsEnabled ? new CameraCheck(i.Id, i.Label, i.Note, was, i.IsWarning, i.IsEnabled) { HasApp = i.HasApp } : i);
        ConnectCameras.Set(items);
        _connectKind = Form.Kind;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        Form.ShowAllErrors();
        if (!Form.IsValid)
        {
            return;
        }

        ErrorText = null;
        InfoText = null;
        IsConnecting = true;
        try
        {
            string? reply = null;
            ErrorText = await GuardedRun.RunAsync(
                async () => reply = await _ctx.InvokeAsync(Fleet.Connect, JsonSerializer.Serialize(Form.ToRequest(false, ConnectCameras.CheckedIds), Fleet.Json), _stop.Token),
                GuardedCancel.Ignore);
            if (ErrorText is null && reply is not null && JsonSerializer.Deserialize<ConnectReply>(reply, Fleet.Json) is { } result)
            {
                Form.ClearSecrets();
                InfoText = (result.Devices == 1 ? "Setting up 1 camera" : $"Setting up {result.Devices:N0} cameras")
                    + $" with {VpnApps.NameOf(Form.Kind)}. Follow the tasks in the task list, then refresh this page.";
            }
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private bool CanConnect() => MayConnect && !IsConnecting && Form.IsFormValid && ConnectCameras.CheckedCount > 0;

    // ---- Install and upgrade ----

    /// <summary>The chosen devices with what the install does to each: outdated ones are ticked, the others not.</summary>
    public IReadOnlyList<CameraCheck> InstallChecklist(VpnKind kind, string? latest)
    {
        var app = VpnApps.Of(kind);
        var list = new List<CameraCheck>();
        foreach (var device in Devices.Resolve())
        {
            var label = DeviceLabel(device);
            if (!_rowsByDevice.TryGetValue(device.Id, out var rows))
            {
                list.Add(new CameraCheck(device.Id, label, "Not read yet", false));
                continue;
            }

            var row = rows.FirstOrDefault(r => r.Vpn == kind);
            if (row?.AppVersion is { } have)
            {
                var newer = latest is not null && AppVersions.IsNewer(latest, have);
                list.Add(new CameraCheck(device.Id, label, newer ? $"Upgrade {have} to {latest}" : $"{have}, the newest", newer) { HasApp = true });
            }
            else if (rows.FirstOrDefault(r => r.Error is not null) is { } failed)
            {
                list.Add(new CameraCheck(device.Id, label, "Not read: " + failed.Error, false, isWarning: true));
            }
            else
            {
                list.Add(new CameraCheck(device.Id, label, latest is null ? "Install" : $"Install {app.DisplayName} {latest}", false));
            }
        }

        return list;
    }

    /// <summary>Starts install or upgrade tasks; the text for the panel, or null with the problem in the page's error.</summary>
    private async Task<string?> StartInstallAsync(VpnKind kind, IReadOnlyList<Guid> deviceIds, bool upgrade)
    {
        if (!MayInstall)
        {
            ErrorText = "Your role cannot install or upgrade VPN apps.";
            return null;
        }

        string? reply = null;
        ErrorText = await GuardedRun.RunAsync(
            async () => reply = await _ctx.InvokeAsync(Fleet.Install, JsonSerializer.Serialize(new InstallRequest(kind, deviceIds, upgrade), Fleet.Json), _stop.Token),
            GuardedCancel.Ignore);
        if (reply is null || JsonSerializer.Deserialize<InstallReply>(reply, Fleet.Json) is not { } result)
        {
            return null;
        }

        var what = upgrade ? $"Upgrading {VpnApps.NameOf(kind)} to {result.Version}" : $"Installing {VpnApps.NameOf(kind)} {result.Version}";
        return $"{what} on {(result.Devices == 1 ? "1 camera" : $"{result.Devices:N0} cameras")}. Follow the tasks in the task list, then refresh this page.";
    }

    // ---- State ----

    /// <summary>Shows a state from the server, unless an equal or newer one is shown already.</summary>
    public void Apply(string? json)
    {
        if (json is null || JsonSerializer.Deserialize<FleetState>(json, Fleet.Json) is not { } state || state.Version <= _version)
        {
            return;
        }

        _version = state.Version;
        IsReading = state.Reading;
        _rowsByDevice = state.Rows.GroupBy(r => r.DeviceId).ToDictionary(g => g.Key, g => g.ToList());
        Rows.ReplaceAll(state.Rows.Select(r => new FleetRowView(r)));
        Summary = SummaryOf(state.Rows, Devices.IsEmpty);
        ReleaseNote = state.ReleaseError is null ? null : "Newest versions unknown: " + state.ReleaseError;
        if (Panel == FleetPanel.Connect)
        {
            RebuildConnectCameras(keepTicks: true);
        }
        else if (Panel == FleetPanel.Install)
        {
            Install.Rebuild();
        }
    }

    public static string SummaryOf(IReadOnlyList<FleetRow> rows, bool nothingChosen)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return nothingChosen ? "Choose devices to see their VPN." : "Click Refresh to read the devices.";
        }

        var devices = rows.Select(r => r.DeviceId).Distinct().Count();
        var parts = new List<string>();
        void Count(int n, string text)
        {
            if (n > 0)
            {
                parts.Add($"{n:N0} {text}");
            }
        }

        Count(rows.Count(r => r.Error is null && r.State == VpnState.Connected), "connected");
        Count(rows.Count(r => r.Error is null && r.State is VpnState.NotConnected or VpnState.Connecting or VpnState.NotSetUp or VpnState.AppStopped or VpnState.Unknown), "not connected");
        Count(rows.Count(r => r.Error is null && r.State == VpnState.NoVpnApp), "without a VPN app");
        Count(rows.Count(r => r.Error is not null), "not read");
        var updates = rows.Count(r => r.UpdateAvailable);
        return (devices == 1 ? "1 device" : $"{devices:N0} devices") + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "")
            + (updates == 0 ? "" : updates == 1 ? ". 1 update" : $". {updates:N0} updates");
    }

    private static string DeviceLabel(IDeviceInfo device) => string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Address} ({device.Model})";

    private void OnFormChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectFormViewModel.IsFormValid) or nameof(ConnectFormViewModel.FormError))
        {
            NotifyCommands();
        }

        if (Panel == FleetPanel.Connect && e.PropertyName is nameof(ConnectFormViewModel.SelectedVpn) or nameof(ConnectFormViewModel.HubDeviceId) or nameof(ConnectFormViewModel.UseHub))
        {
            RebuildConnectCameras(keepTicks: _connectKind == Form.Kind);
        }
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ConnectBlockedReason));
        OnPropertyChanged(nameof(ConnectText));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
