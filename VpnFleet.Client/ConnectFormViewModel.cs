using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Validation;

namespace VpnFleet.Client;

/// <summary>
/// The form "Connect to a VPN": the VPN and its settings. The rules are those of the server
/// (<see cref="ConnectRules"/>), so the page never sends what the server refuses.
/// </summary>
public sealed partial class ConnectFormViewModel : ValidatingViewModel
{
    private static readonly string[] Fields =
    [
        nameof(AuthKey), nameof(LoginServer), nameof(SetupKey), nameof(ManagementUrl), nameof(NetworkId), nameof(Profile),
        nameof(UserName), nameof(Password), nameof(Endpoint), nameof(ServerPublicKey), nameof(AddressPool), nameof(AllowedIps),
    ];

    public ConnectFormViewModel()
    {
        SelectedVpn = Vpns[0];
        Validation.Rules(Fields, () =>
        {
            var errors = ConnectRules.Validate(ToRequest(false, []));
            return Fields.ToDictionary(f => f, f => errors.FirstOrDefault(e => e.Field == f)?.Message);
        });
        Validation.Rule(nameof(HubName), () => Kind == VpnKind.WireGuard && UseHub && HubDeviceId is null ? "Choose the camera that runs WireGuard Server." : null);
        Validation.Validate();
    }

    public IReadOnlyList<VpnChoice> Vpns { get; } = [.. VpnApps.All.Where(a => a.CanConnect).Select(a => new VpnChoice(a.Kind, a.DisplayName))];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTailscale), nameof(IsNetBird), nameof(IsZeroTier), nameof(IsWireGuard), nameof(IsOpenVpn), nameof(IsWireGuardHub), nameof(IsWireGuardOther))]
    public partial VpnChoice SelectedVpn { get; set; }

    public VpnKind Kind => SelectedVpn.Kind;

    public bool IsTailscale => Kind == VpnKind.Tailscale;

    public bool IsNetBird => Kind == VpnKind.NetBird;

    public bool IsZeroTier => Kind == VpnKind.ZeroTier;

    public bool IsWireGuard => Kind == VpnKind.WireGuard;

    public bool IsOpenVpn => Kind == VpnKind.OpenVpn;

    public bool IsWireGuardHub => IsWireGuard && UseHub;

    public bool IsWireGuardOther => IsWireGuard && !UseHub;

    [ObservableProperty]
    public partial string AuthKey { get; set; } = "";

    [ObservableProperty]
    public partial string LoginServer { get; set; } = "";

    [ObservableProperty]
    public partial string SetupKey { get; set; } = "";

    [ObservableProperty]
    public partial string ManagementUrl { get; set; } = "";

    [ObservableProperty]
    public partial string NetworkId { get; set; } = "";

    /// <summary>The text of the chosen .ovpn file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileDetails))]
    public partial string Profile { get; set; } = "";

    [ObservableProperty]
    public partial string? ProfileName { get; set; }

    public string? ProfileDetails => Profile.Length == 0 ? null : $"{Profile.Length / 1024.0:0.#} KB";

    [ObservableProperty]
    public partial string UserName { get; set; } = "";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    /// <summary>WireGuard: true = a camera with WireGuard Server is the hub, false = another WireGuard server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWireGuardHub), nameof(IsWireGuardOther))]
    public partial bool UseHub { get; set; } = true;

    [ObservableProperty]
    public partial Guid? HubDeviceId { get; set; }

    [ObservableProperty]
    public partial string? HubName { get; set; }

    [ObservableProperty]
    public partial string Endpoint { get; set; } = "";

    [ObservableProperty]
    public partial string ServerPublicKey { get; set; } = "";

    [ObservableProperty]
    public partial string AddressPool { get; set; } = "";

    [ObservableProperty]
    public partial string AllowedIps { get; set; } = "";

    /// <summary>Opens the device picker for the hub; set by the view, which knows the window.</summary>
    public Func<Guid?, Task<Guid?>>? PickHub { get; set; }

    /// <summary>Opens a file dialog for the profile: its name and text, null when cancelled. Set by the view.</summary>
    public Func<Task<(string Name, string Text)?>>? PickProfile { get; set; }

    /// <summary>The names of the devices, to show the chosen hub; set by the page.</summary>
    public Func<Guid, string?>? NameOf { get; set; }

    [RelayCommand]
    private async Task ChooseHubAsync()
    {
        if (PickHub is { } pick && await pick(HubDeviceId) is { } id)
        {
            HubDeviceId = id;
            HubName = NameOf?.Invoke(id) ?? id.ToString();
        }
    }

    [RelayCommand]
    private async Task ChooseProfileAsync()
    {
        if (PickProfile is { } pick && await pick() is { } file)
        {
            ProfileName = file.Name;
            Profile = file.Text;
        }
    }

    /// <summary>Shows every error of the chosen VPN (before a submit).</summary>
    public void ShowAllErrors() => Validation.ShowAll([.. Fields, nameof(HubName)]);

    public bool IsValid => Validation.IsValid;

    /// <summary>The profile and the hub are chosen with a button, not typed: their fields show these errors.</summary>
    public string? ProfileError => ErrorOf(nameof(Profile));

    public string? HubError => ErrorOf(nameof(HubName));

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(ProfileError));
        OnPropertyChanged(nameof(HubError));
    }

    public ConnectRequest ToRequest(bool all, IReadOnlyList<Guid> deviceIds) => new()
    {
        Kind = Kind,
        All = all,
        DeviceIds = deviceIds,
        AuthKey = AuthKey.Trim(),
        LoginServer = LoginServer.Trim(),
        SetupKey = SetupKey.Trim(),
        ManagementUrl = ManagementUrl.Trim(),
        NetworkId = NetworkId.Trim(),
        Profile = Profile,
        UserName = UserName.Trim(),
        Password = Password,
        HubDeviceId = UseHub ? HubDeviceId : null,
        Endpoint = UseHub ? null : Endpoint.Trim(),
        ServerPublicKey = UseHub ? null : ServerPublicKey.Trim(),
        AddressPool = UseHub ? null : AddressPool.Trim(),
        AllowedIps = AllowedIps.Trim(),
    };

    /// <summary>Clears the secrets once they were sent: they never stay in the page.</summary>
    public void ClearSecrets()
    {
        AuthKey = "";
        SetupKey = "";
        Password = "";
        Validation.Reset(nameof(AuthKey), nameof(SetupKey), nameof(Password));
    }
}
