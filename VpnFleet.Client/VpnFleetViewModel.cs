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

/// <summary>
/// The page: the VPN apps of the chosen devices and their tunnels, and the form that connects devices to a VPN. Reads
/// the state from the server part, then follows its events. Nothing is read before a device choice.
/// </summary>
public sealed partial class VpnFleetViewModel : ObservableObject, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private int _version = -1;

    public VpnFleetViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx;
        Devices = new PageDeviceChoice(ctx);
        Devices.PropertyChanged += (_, _) => NotifyCommands();
        Form = new ConnectFormViewModel { NameOf = id => _ctx.Devices.FirstOrDefault(d => d.Id == id) is { } d ? DeviceLabel(d) : null };
        Form.PropertyChanged += OnFormChanged;
        MayConnect = ctx.Has(PermissionKeys.Of(Fleet.PluginId, Fleet.ConnectPermission));
    }

    public PageDeviceChoice Devices { get; }

    public ConnectFormViewModel Form { get; }

    /// <summary>Opens the device picker; set by the view, which knows the window.</summary>
    public Func<Task<bool>>? PickDevices { get; set; }

    public RangeObservableCollection<FleetRowView> Rows { get; } = [];

    /// <summary>The role may change VPN settings on devices.</summary>
    public bool MayConnect { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsReading { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial bool IsConnecting { get; private set; }

    [ObservableProperty]
    public partial bool IsConnectOpen { get; set; }

    [ObservableProperty]
    public partial bool IsPeersOpen { get; set; }

    [ObservableProperty]
    public partial string PeersText { get; private set; } = "";

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    public partial string? InfoText { get; private set; }

    /// <summary>"12 devices: 9 connected, 2 not connected, 1 without a VPN app".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    /// <summary>Why Connect is off, for its tooltip.</summary>
    public string? ConnectBlockedReason => Devices.IsEmpty ? Fleet.NothingChosen : Form.FormError;

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
        ErrorText = await GuardedRun.RunAsync(
            () => _ctx.InvokeAsync(Fleet.Refresh, JsonSerializer.Serialize(new RefreshRequest(All, Ids()), Fleet.Json), _stop.Token),
            GuardedCancel.Ignore);
    }

    private bool CanRefresh() => !IsReading && !Devices.IsEmpty;

    [RelayCommand]
    private void ToggleConnect()
    {
        IsConnectOpen = !IsConnectOpen;
        IsPeersOpen = false;
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
                async () => reply = await _ctx.InvokeAsync(Fleet.Connect, JsonSerializer.Serialize(Form.ToRequest(All, Ids()), Fleet.Json), _stop.Token),
                GuardedCancel.Ignore);
            if (ErrorText is null && reply is not null && JsonSerializer.Deserialize<ConnectReply>(reply, Fleet.Json) is { } result)
            {
                Form.ClearSecrets();
                InfoText = (result.Devices == 1 ? "Connecting 1 device" : $"Connecting {result.Devices:N0} devices")
                    + $" to {VpnApps.NameOf(Form.Kind)}. Follow the tasks in the task list, then refresh this page.";
            }
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private bool CanConnect() => MayConnect && !IsConnecting && !Devices.IsEmpty && Form.IsFormValid;

    [RelayCommand]
    private async Task ShowPeersAsync()
    {
        IsPeersOpen = !IsPeersOpen;
        IsConnectOpen = false;
        if (!IsPeersOpen)
        {
            return;
        }

        string? reply = null;
        ErrorText = await GuardedRun.RunAsync(async () => reply = await _ctx.InvokeAsync(Fleet.GetPeers, null, _stop.Token), GuardedCancel.Ignore);
        if (reply is not null && JsonSerializer.Deserialize<PeersReply>(reply, Fleet.Json) is { } peers)
        {
            PeersText = peers.Count == 0
                ? "No camera uses another WireGuard server yet. Connect cameras with WireGuard and \"Another WireGuard server\" first."
                : peers.Text;
        }
    }

    /// <summary>Shows a state from the server, unless an equal or newer one is shown already.</summary>
    public void Apply(string? json)
    {
        if (json is null || JsonSerializer.Deserialize<FleetState>(json, Fleet.Json) is not { } state || state.Version <= _version)
        {
            return;
        }

        _version = state.Version;
        IsReading = state.Reading;
        Rows.ReplaceAll(state.Rows.Select(r => new FleetRowView(r)));
        Summary = SummaryOf(state.Rows, Devices.IsEmpty);
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
        return (devices == 1 ? "1 device" : $"{devices:N0} devices") + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");
    }

    private bool All => Devices.Choice.Kind == DeviceChoiceKind.All;

    private List<Guid> Ids() => All ? [] : [.. Devices.Resolve().Select(d => d.Id)];

    private static string DeviceLabel(IDeviceInfo device) => string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Address} ({device.Model})";

    private void OnFormChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectFormViewModel.IsFormValid) or nameof(ConnectFormViewModel.FormError))
        {
            NotifyCommands();
        }
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ConnectBlockedReason));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
