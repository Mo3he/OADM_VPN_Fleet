using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace VpnFleet;

/// <summary>One run of <see cref="InstallTaskPlugin"/>: the app and the release version the page showed.</summary>
public sealed record InstallPayload(VpnKind Kind, string Version, bool Upgrade);

/// <summary>
/// Installs or upgrades one app on one device from its newest GitHub release: chooses the package for the device's
/// architecture and AXIS OS, downloads it (once for all devices), installs it, starts the app and checks the version.
/// A device that has the version already is not changed. Started only from the page.
/// </summary>
public sealed class InstallTaskPlugin : ITaskPlugin
{
    public const string PluginId = Fleet.PluginId + ".install";

    public const string CheckStep = "Check the device";
    public const string DownloadStep = "Download the package";
    public const string InstallStep = "Install the package";
    public const string StartStep = "Start the app";
    public const string VerifyStep = "Check the installed version";

    private readonly Func<ReleaseCatalog> _catalog;

    internal InstallTaskPlugin(Func<ReleaseCatalog> catalog)
    {
        _catalog = catalog;
    }

    public string Id => PluginId;

    public string DisplayName => "Install a VPN app";

    public string? IconKey => "app";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool ShowInMenus => false;

    /// <summary>Uploads of packages are large: at most 4 devices at a time.</summary>
    public int? MaxParallelDevices => 4;

    public string GetTaskName(string? payloadJson)
    {
        try
        {
            var p = JsonSerializer.Deserialize<InstallPayload>(payloadJson ?? "null", Fleet.Json);
            return p is null ? DisplayName
                : p.Upgrade ? $"Upgrade {VpnApps.NameOf(p.Kind)} to {p.Version}"
                : $"Install {VpnApps.NameOf(p.Kind)} {p.Version}";
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
        var payload = JsonSerializer.Deserialize<InstallPayload>(payloadJson ?? "null", Fleet.Json)
            ?? throw new ArgumentException("The task has no app. Nothing was changed.");
        var app = VpnApps.Of(payload.Kind);
        ctx.PlanSteps(CheckStep, DownloadStep, InstallStep, StartStep, VerifyStep);

        var (release, error) = await _catalog().GetAsync(payload.Kind, checkNow: false, ct).ConfigureAwait(false);
        if (release is null)
        {
            throw new InvalidOperationException((error ?? "The release could not be read.") + " Nothing was changed.");
        }

        var (asset, installed) = await ctx.StepAsync(CheckStep, async step =>
        {
            var info = await ctx.Vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            var apps = await DeviceVpn.ListAppsAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var current = apps.FirstOrDefault(a => string.Equals(a.Name, app.AppName, StringComparison.OrdinalIgnoreCase));
            var (chosen, reason) = ReleaseCatalog.ChooseAsset(release, info.Architecture ?? device.Architecture, info.Version);
            if (chosen is null)
            {
                throw new VpnDeviceException(reason + " Nothing was changed.");
            }

            step.Complete($"{info.Architecture}, AXIS OS {info.Version}" + (current?.Version is { } v ? $", {app.DisplayName} {v}" : ""));
            return (chosen, current);
        }).ConfigureAwait(false);

        if (installed?.Version is { } have && !AppVersions.IsNewer(release.Version, have))
        {
            var why = AppVersions.Compare(have, release.Version) == 0 ? $"{app.DisplayName} {have} is installed already." : $"{app.DisplayName} {have} is newer than the release {release.Version}.";
            foreach (var name in new[] { DownloadStep, InstallStep, StartStep, VerifyStep })
            {
                ctx.SkipStep(name, why);
            }

            ctx.Log(TaskLogLevel.Info, why + " Nothing was changed.");
            return;
        }

        var file = await ctx.StepAsync(DownloadStep, async step =>
        {
            var progress = new Progress<long>(bytes => step.ReportProgress(asset.Size > 0 ? (int)(bytes * 100 / asset.Size) : 0));
            var path = await _catalog().DownloadAsync(asset, progress, ct).ConfigureAwait(false);
            step.Complete(asset.Name);
            return path;
        }).ConfigureAwait(false);

        await ctx.StepAsync(InstallStep, async step =>
        {
            await DeviceVpn.InstallPackageAsync(ctx.Vapix, file, ct).ConfigureAwait(false);
            step.Complete(installed is null ? $"Installed {release.Version}" : $"Upgraded from {installed.Version}");
        }).ConfigureAwait(false);

        var after = (await DeviceVpn.ListAppsAsync(ctx.Vapix, ct).ConfigureAwait(false))
            .FirstOrDefault(a => string.Equals(a.Name, app.AppName, StringComparison.OrdinalIgnoreCase));
        if (after is { IsRunning: false })
        {
            await ctx.StepAsync(StartStep, _ => DeviceVpn.StartAppAsync(ctx.Vapix, app.AppName, ct)).ConfigureAwait(false);
        }
        else
        {
            ctx.SkipStep(StartStep, "The app runs already.");
        }

        using var verify = ctx.BeginStep(VerifyStep);
        if (after?.Version is { } now && AppVersions.Compare(now, release.Version) == 0)
        {
            verify.Complete($"{app.DisplayName} {now}");
            ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} {now} is installed ({asset.Name}).");
            if (installed is null)
            {
                ctx.Log(TaskLogLevel.Info, "Set up the VPN on the VPN Fleet page.");
            }
        }
        else
        {
            verify.Warn($"The device reports version {after?.Version ?? "none"}, expected {release.Version}.");
        }
    }
}
