using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;

namespace VpnFleet.Client;

/// <summary>
/// The panel "Install or upgrade": an app, its newest release on GitHub and the cameras it goes to, each with what
/// happens to it (install, upgrade from which version, up to date).
/// </summary>
public sealed partial class InstallViewModel : ObservableObject
{
    private readonly ICorePluginClientContext _ctx;
    private readonly CancellationToken _stop;
    private Dictionary<VpnKind, ReleaseInfo> _releases = [];

    public InstallViewModel(ICorePluginClientContext ctx, CancellationToken stop)
    {
        _ctx = ctx;
        _stop = stop;
        SelectedApp = Apps[0];
        Cameras.Changed += (_, _) => NotifyInstall();
    }

    public IReadOnlyList<VpnChoice> Apps { get; } = [.. VpnApps.All.Select(a => new VpnChoice(a.Kind, a.DisplayName))];

    [ObservableProperty]
    public partial VpnChoice SelectedApp { get; set; }

    public CameraChecklist Cameras { get; } = new();

    /// <summary>The page's devices and rows, to build the checklist; set by the page.</summary>
    public Func<VpnKind, string?, IEnumerable<CameraCheck>>? BuildChecklist { get; set; }

    /// <summary>Starts the install (the page owns it): app, devices, upgrade.</summary>
    public Func<VpnKind, IReadOnlyList<Guid>, bool, Task<string?>>? StartInstall { get; set; }

    public Action<string>? OpenUrl { get; set; }

    [ObservableProperty]
    public partial string ReleaseText { get; private set; } = "";

    [ObservableProperty]
    public partial string? ReleaseUrl { get; private set; }

    [ObservableProperty]
    public partial bool IsChecking { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool IsStarting { get; private set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    public partial string? InfoText { get; private set; }

    public string? LatestVersion => _releases.GetValueOrDefault(SelectedApp.Kind)?.Version;

    public string InstallText => Cameras.CheckedCount == 1 ? "Install or upgrade 1 camera" : $"Install or upgrade {Cameras.CheckedCount:N0} cameras";

    public string? InstallBlockedReason => LatestVersion is null ? "The newest version is not known yet." : Cameras.CheckedCount == 0 ? "Tick the cameras first." : null;

    partial void OnSelectedAppChanged(VpnChoice value) => Rebuild();

    /// <summary>Reads the newest releases (from the server's cache unless <paramref name="checkNow"/>), then shows the chosen app's.</summary>
    public async Task LoadReleasesAsync(bool checkNow)
    {
        IsChecking = true;
        try
        {
            string? reply = null;
            ErrorText = await GuardedRun.RunAsync(
                async () => reply = await _ctx.InvokeAsync(Fleet.GetReleases, JsonSerializer.Serialize(new ReleasesRequest(checkNow), Fleet.Json), _stop),
                GuardedCancel.Ignore);
            if (reply is not null && JsonSerializer.Deserialize<List<ReleaseInfo>>(reply, Fleet.Json) is { } list)
            {
                _releases = list.ToDictionary(r => r.Kind);
            }
        }
        finally
        {
            IsChecking = false;
            Rebuild();
        }
    }

    public void Rebuild()
    {
        var release = _releases.GetValueOrDefault(SelectedApp.Kind);
        ReleaseUrl = release?.PageUrl;
        ReleaseText = release switch
        {
            null => IsChecking ? "Checking GitHub for the newest version." : "",
            { Version: null } => release.Error ?? "The newest version is not known.",
            _ => $"Newest version: {release.Version}" + (release.Published is { } at ? $", released {at.LocalDateTime:d}." : "."),
        };
        Cameras.Set(BuildChecklist?.Invoke(SelectedApp.Kind, release?.Version) ?? []);
        NotifyInstall();
    }

    private void NotifyInstall()
    {
        OnPropertyChanged(nameof(InstallText));
        OnPropertyChanged(nameof(InstallBlockedReason));
        OnPropertyChanged(nameof(LatestVersion));
        InstallCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private Task CheckNowAsync() => LoadReleasesAsync(true);

    [RelayCommand]
    private void OpenRelease()
    {
        if (ReleaseUrl is { } url)
        {
            OpenUrl?.Invoke(url);
        }
    }

    private bool CanInstall() => !IsStarting && InstallBlockedReason is null && StartInstall is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        IsStarting = true;
        ErrorText = null;
        InfoText = null;
        try
        {
            var upgrade = Cameras.Items.Where(i => i.IsChecked).All(i => i.HasApp);
            InfoText = await StartInstall!(SelectedApp.Kind, Cameras.CheckedIds, upgrade);
        }
        finally
        {
            IsStarting = false;
        }
    }
}
