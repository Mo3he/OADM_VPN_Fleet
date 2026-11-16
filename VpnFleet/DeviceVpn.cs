using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;

using Oadm.Sdk.Vapix;

namespace VpnFleet;

/// <summary>An app installed on a device (applications/list.cgi).</summary>
public sealed record InstalledApp(string Name, string? Version, string? Status)
{
    public bool IsRunning => string.Equals(Status, "Running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The WireGuard Server app of a hub camera: its key and network as the cameras need them.</summary>
public sealed record HubInfo(string PublicKey, AddressPool Pool, string Endpoint, string? Peers, bool Enabled);

/// <summary>A device answered with something other than success.</summary>
public sealed class VpnDeviceException(string message) : Exception(message);

/// <summary>Every device request of the plugin, through the logged-in VAPIX client OADM gives it.</summary>
public static class DeviceVpn
{
    public const string AppListPath = "/axis-cgi/applications/list.cgi";
    public const string AppControlPath = "/axis-cgi/applications/control.cgi";
    public const string ParamPath = "/axis-cgi/param.cgi";
    public const string SystemLogPath = "/axis-cgi/admin/systemlog.cgi";

    public static async Task<IReadOnlyList<InstalledApp>> ListAppsAsync(IVapixClient vapix, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        var (status, body) = await GetAsync(vapix, AppListPath, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            throw new VpnDeviceException("The device did not list its apps (HTTP " + (int)status + ").");
        }

        return ParseAppList(body);
    }

    public static IReadOnlyList<InstalledApp> ParseAppList(string xml)
    {
        try
        {
            var root = DeviceXml.ParseElement(xml.Trim());
            return [.. root.Elements("application")
                .Select(a => new InstalledApp((string?)a.Attribute("Name") ?? "", (string?)a.Attribute("Version"), (string?)a.Attribute("Status")))
                .Where(a => a.Name.Length > 0)];
        }
        catch (XmlException)
        {
            throw new VpnDeviceException("The device's app list could not be read.");
        }
    }

    public static async Task StartAppAsync(IVapixClient vapix, string appName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        var (status, body) = await GetAsync(vapix, AppControlPath + "?action=start&package=" + Uri.EscapeDataString(appName), ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK || !body.TrimStart().StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new VpnDeviceException("The device did not start the app: " + Short(body));
        }
    }

    /// <summary>Writes parameters with param.cgi in one request; the values travel in the POST body, never in the URL.</summary>
    public static async Task SetParametersAsync(IVapixClient vapix, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(values);
        var form = new List<KeyValuePair<string, string>> { new("action", "update") };
        form.AddRange(values);
        using var request = new HttpRequestMessage(HttpMethod.Post, ParamPath) { Content = new FormUrlEncodedContent(form) };
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || !body.TrimStart().StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new VpnDeviceException("The device refused the settings: " + Short(body));
        }
    }

    public static async Task UploadOpenVpnProfileAsync(IVapixClient vapix, string profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        using var request = new HttpRequestMessage(HttpMethod.Post, VpnApps.OpenVpn.LocalPath("api/profile"))
        {
            Content = new StringContent(profile, Encoding.UTF8, "application/octet-stream"),
        };
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new VpnDeviceException("The OpenVPN app did not take the profile: " + Short(body));
        }
    }

    /// <summary>The tunnel status of a running app; a read that fails is a status too (the app may still be starting).</summary>
    public static async Task<VpnStatus> ReadStatusAsync(IVapixClient vapix, VpnApp app, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(app);
        var path = app.Kind switch
        {
            VpnKind.NetBird => app.LocalPath("api/status"),
            VpnKind.WireGuard => SystemLogPath + "?appname=" + Uri.EscapeDataString(app.AppName),
            _ => app.LocalPath("status.json"),
        };
        var (status, body) = await GetAsync(vapix, path, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound && app.Kind != VpnKind.NetBird)
        {
            return new VpnStatus(VpnState.Unknown, null, "This version of the app reports no status. Update the app.");
        }

        return VpnStatusParser.Parse(app.Kind, body);
    }

    /// <summary>Reads the hub: the WireGuard Server app must be installed, running and have its key.</summary>
    public static async Task<HubInfo> ReadHubAsync(IVapixClient vapix, string hubAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        var apps = await ListAppsAsync(vapix, ct).ConfigureAwait(false);
        var app = apps.FirstOrDefault(a => string.Equals(a.Name, VpnApps.WireGuardServerAppName, StringComparison.OrdinalIgnoreCase))
            ?? throw new VpnDeviceException("The hub camera has no WireGuard Server app. Install it there first.");
        if (!app.IsRunning)
        {
            throw new VpnDeviceException("The WireGuard Server app on the hub camera is not running. Start it first.");
        }

        var prefix = VpnApps.WireGuardServerAppName + ".";
        var parameters = await vapix.ListParametersAsync([VpnApps.WireGuardServerAppName], ct).ConfigureAwait(false);
        string? Param(string name) => parameters.TryGetValue(prefix + name, out var v) ? v : null;

        var (_, body) = await GetAsync(vapix, "/local/" + VpnApps.WireGuardServerAppName + "/status", ct).ConfigureAwait(false);
        string? publicKey = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("publicKey", out var key) && key.ValueKind == JsonValueKind.String)
            {
                publicKey = key.GetString();
            }
        }
        catch (JsonException)
        {
        }

        if (!WireGuardKeys.IsKey(publicKey))
        {
            throw new VpnDeviceException("The WireGuard server on the hub camera has no key yet. Turn the server on in its app first.");
        }

        var pool = WireGuardAddressing.ParsePool(Param("Address"))
            ?? throw new VpnDeviceException("The WireGuard server on the hub camera has no valid tunnel address.");
        var port = Param("ListenPort") is { Length: > 0 } p ? p.Trim() : "51820";
        var endpoint = Param("PublicEndpoint") is { Length: > 0 } pub ? pub.Trim() : hubAddress + ":" + port;
        if (!endpoint.Contains(':', StringComparison.Ordinal))
        {
            endpoint += ":" + port;
        }

        var enabled = string.Equals(Param("Enabled"), "yes", StringComparison.OrdinalIgnoreCase);
        return new HubInfo(publicKey!, pool, endpoint, Param("Peers"), enabled);
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(IVapixClient vapix, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static string Short(string body)
    {
        var text = body.Trim().ReplaceLineEndings(" ");
        return text.Length == 0 ? "no answer." : text.Length > 160 ? text[..160] + "…" : text;
    }
}
