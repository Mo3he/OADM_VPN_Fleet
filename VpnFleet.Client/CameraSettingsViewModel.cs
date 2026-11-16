using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;

namespace VpnFleet.Client;

/// <summary>One setting of the camera panel: its value, whether the user changed it, and its problem.</summary>
public sealed partial class SettingFieldViewModel : ObservableObject
{
    private readonly VpnApp _app;

    public SettingFieldViewModel(VpnApp app, SettingValue value)
    {
        _app = app;
        Source = value;
        Original = value.Value;
        Value = value.Value;
        SelectedOption = value.Options.FirstOrDefault(o => string.Equals(o.Value, value.Value, StringComparison.OrdinalIgnoreCase));
    }

    public SettingValue Source { get; }

    public string Name => Source.Name;

    public string Label => Source.Label;

    public string? Hint => Source.Hint;

    public IReadOnlyList<SettingOption> Options => Source.Options;

    public bool IsText => Source.Kind == SettingKind.Text;

    public bool IsSecret => Source.Kind == SettingKind.Secret;

    public bool IsBool => Source.Kind == SettingKind.Bool;

    public bool IsChoice => Source.Kind == SettingKind.Choice;

    /// <summary>A secret shows whether it is set, never its value.</summary>
    public string? Placeholder => IsSecret ? (Source.HasValue ? "Set. Enter a new one to replace it." : "Not set") : Source.Placeholder;

    public string Original { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(BoolValue))]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial SettingOption? SelectedOption { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public bool BoolValue
    {
        get => Options.Count > 0 && string.Equals(Value, Options[0].Value, StringComparison.OrdinalIgnoreCase);
        set => Value = Options.Count < 2 ? Value : value ? Options[0].Value : Options[1].Value;
    }

    /// <summary>Changed by the user; an empty secret is no change.</summary>
    public bool IsDirty => IsSecret ? Value.Length > 0 : !string.Equals(Value.Trim(), Original.Trim(), StringComparison.Ordinal);

    partial void OnSelectedOptionChanged(SettingOption? value)
    {
        if (value is not null)
        {
            Value = value.Value;
        }
    }

    partial void OnValueChanged(string value) =>
        Error = IsDirty ? AppSettings.Validate(_app, new Dictionary<string, string> { [Name] = value }, [Name]).FirstOrDefault()?.Message : null;

    /// <summary>After a save: the saved value is the new original.</summary>
    public void Accept()
    {
        Original = IsSecret ? "" : Value;
        if (IsSecret)
        {
            Value = "";
        }

        OnPropertyChanged(nameof(IsDirty));
    }
}

/// <summary>
/// The panel of one selected row: the app's version and upgrade, its settings with Save for this camera only, and for
/// a camera without a VPN app the install of one.
/// </summary>
public sealed partial class CameraSettingsViewModel : ObservableObject
{
    private readonly ICorePluginClientContext _ctx;
    private readonly CancellationToken _stop;

    public CameraSettingsViewModel(ICorePluginClientContext ctx, FleetRowView row, bool mayChange, bool mayInstall, CancellationToken stop)
    {
        _ctx = ctx;
        _stop = stop;
        Row = row;
        MayChange = mayChange;
        MayInstall = mayInstall;
        App = row.Row.Vpn is { } kind ? VpnApps.Of(kind) : null;
        SelectedInstallApp = InstallApps[0];
    }

    public FleetRowView Row { get; }

    public VpnApp? App { get; }

    public bool HasApp => App is not null && Row.Row.Error is null;

    public bool HasNoApp => Row.Row.Error is null && Row.Row.State == VpnState.NoVpnApp;

    public bool MayChange { get; }

    public bool MayInstall { get; }

    public string Title => App is null ? Row.Address : $"{App.DisplayName} on {Row.Address}";

    public string VersionText => Row.Row.AppVersion is null ? ""
        : Row.Row.UpdateAvailable ? $"Version {Row.Row.AppVersion}. Version {Row.Row.LatestVersion} is available."
        : Row.Row.LatestVersion is null ? $"Version {Row.Row.AppVersion}."
        : $"Version {Row.Row.AppVersion}, the newest.";

    public bool CanUpgrade => MayInstall && Row.Row.UpdateAvailable;

    public string UpgradeText => $"Upgrade to {Row.Row.LatestVersion}";

    public IReadOnlyList<VpnChoice> InstallApps { get; } = [.. VpnApps.All.Select(a => new VpnChoice(a.Kind, a.DisplayName))];

    [ObservableProperty]
    public partial VpnChoice SelectedInstallApp { get; set; }

    public System.Collections.ObjectModel.ObservableCollection<SettingFieldViewModel> Fields { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    public partial string? InfoText { get; private set; }

    /// <summary>WireGuard: the camera's public key, for its server.</summary>
    [ObservableProperty]
    public partial string? PublicKey { get; private set; }

    public bool IsWireGuard => App?.Kind is VpnKind.WireGuard or VpnKind.WireGuardServer;

    public bool IsOpenVpn => App?.Kind == VpnKind.OpenVpn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string? Profile { get; private set; }

    [ObservableProperty]
    public partial string? ProfileName { get; private set; }

    /// <summary>Opens a file dialog for the OpenVPN profile; set by the view.</summary>
    public Func<Task<(string Name, string Text)?>>? PickProfile { get; set; }

    /// <summary>Opens the release page; set by the view.</summary>
    public Action<string>? OpenUrl { get; set; }

    /// <summary>Starts an install or upgrade (the page owns it): app, devices, upgrade.</summary>
    public Func<VpnKind, IReadOnlyList<Guid>, bool, Task<string?>>? StartInstall { get; set; }

    public async Task LoadAsync()
    {
        if (!HasApp)
        {
            return;
        }

        IsLoading = true;
        ErrorText = null;
        try
        {
            string? reply = null;
            ErrorText = await GuardedRun.RunAsync(
                async () => reply = await _ctx.InvokeAsync(Fleet.GetSettings, JsonSerializer.Serialize(new SettingsRequest(Row.Row.DeviceId, App!.Kind), Fleet.Json), _stop),
                GuardedCancel.Ignore);
            if (reply is not null && JsonSerializer.Deserialize<SettingsReply>(reply, Fleet.Json) is { } settings)
            {
                foreach (var old in Fields)
                {
                    old.PropertyChanged -= OnFieldChanged;
                }

                Fields.Clear();
                foreach (var value in settings.Fields)
                {
                    var field = new SettingFieldViewModel(App!, value);
                    field.PropertyChanged += OnFieldChanged;
                    Fields.Add(field);
                }

                PublicKey = settings.PublicKey;
            }
        }
        finally
        {
            IsLoading = false;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingFieldViewModel.IsDirty) or nameof(SettingFieldViewModel.Error))
        {
            SaveCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SaveBlockedReason));
        }
    }

    public string? SaveBlockedReason =>
        !MayChange ? "Your role cannot change VPN settings."
        : Fields.FirstOrDefault(f => f.Error is not null)?.Error
        ?? (Fields.Any(f => f.IsDirty) || Profile is not null ? null : "Change a setting first.");

    private bool CanSave() => !IsSaving && HasApp && SaveBlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsSaving = true;
        ErrorText = null;
        InfoText = null;
        try
        {
            var changed = Fields.Where(f => f.IsDirty).ToList();
            var request = new SaveSettingsRequest(Row.Row.DeviceId, App!.Kind, changed.ToDictionary(f => f.Name, f => f.Value)) { Profile = Profile };
            string? reply = null;
            ErrorText = await GuardedRun.RunAsync(
                async () => reply = await _ctx.InvokeAsync(Fleet.SaveSettings, JsonSerializer.Serialize(request, Fleet.Json), _stop),
                GuardedCancel.Ignore);
            if (reply is not null && JsonSerializer.Deserialize<SaveSettingsReply>(reply, Fleet.Json) is { } saved)
            {
                foreach (var field in changed)
                {
                    field.Accept();
                }

                Profile = null;
                ProfileName = null;
                InfoText = (saved.Changed == 1 ? "Saved 1 setting. The app restarts with it." : $"Saved {saved.Changed} settings. The app restarts with them.")
                    + (saved.Note is null ? "" : " " + saved.Note);
                if (changed.Any(f => f.Name == "PrivateKey"))
                {
                    await LoadAsync();
                }
            }
        }
        finally
        {
            IsSaving = false;
            OnPropertyChanged(nameof(SaveBlockedReason));
        }
    }

    /// <summary>WireGuard: a new key pair; the private key goes into its field, the public key shows for the server.</summary>
    [RelayCommand]
    private void NewKeyPair()
    {
        if (Fields.FirstOrDefault(f => f.Name == "PrivateKey") is not { } field)
        {
            return;
        }

        var keys = WireGuardKeys.Generate();
        field.Value = keys.PrivateKey;
        PublicKey = keys.PublicKey;
        InfoText = "New key made. Save to use it.";
    }

    [RelayCommand]
    private async Task ChooseProfileAsync()
    {
        if (PickProfile is { } pick && await pick() is { } file)
        {
            ErrorText = ConnectRules.ProfileProblem(file.Text);
            if (ErrorText is null)
            {
                ProfileName = file.Name;
                Profile = file.Text;
                OnPropertyChanged(nameof(SaveBlockedReason));
            }
        }
    }

    [RelayCommand]
    private async Task UpgradeAsync()
    {
        if (StartInstall is { } start && App is not null)
        {
            ErrorText = null;
            InfoText = await start(App.Kind, [Row.Row.DeviceId], true);
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (StartInstall is { } start)
        {
            ErrorText = null;
            InfoText = await start(SelectedInstallApp.Kind, [Row.Row.DeviceId], false);
        }
    }

    [RelayCommand]
    private void OpenRelease()
    {
        if (App is not null)
        {
            OpenUrl?.Invoke($"https://github.com/{VpnApps.Owner}/{App.Repository}/releases/latest");
        }
    }
}
