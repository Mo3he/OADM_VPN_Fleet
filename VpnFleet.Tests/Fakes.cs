using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace VpnFleet.Tests;

/// <summary>The page's context: calls go straight to the server part, events come from the server's fake context (Oadm.Sdk.Testing).</summary>
internal sealed class FakePageContext(VpnFleetPlugin plugin, FakeCoreContext server) : ICorePluginClientContext
{
    public IReadOnlyList<IDeviceInfo> Devices => server.DeviceList;

    public IReadOnlyList<IDeviceInfo> SelectedDevices { get; set; } = [];

    public HashSet<string> Denied { get; } = [];

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        server.InvokeAsync(plugin, server.CallAsAllDevices(), method, payloadJson, ct);

    public bool Has(string permission) => !Denied.Contains(permission);

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in server.Published.Reader.ReadAllAsync(ct))
        {
            yield return item;
        }
    }
}

/// <summary>
/// A camera with apps: answers the app list, app start and the files and APIs of the VPN apps from <see cref="Files"/>
/// ("/local/Tailscale_VPN/status.json" = text). param.cgi is the <see cref="FakeCamera"/>'s.
/// </summary>
internal sealed class AppCamera : FakeCamera
{
    public AppCamera()
    {
        Respond = Answer;
    }

    /// <summary>Installed apps: name, version, status.</summary>
    public List<(string Name, string Version, string Status)> Apps { get; } = [];

    /// <summary>Answers by path (without the query); a missing path answers 404.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Request bodies by path, in order.</summary>
    public List<(string Path, string Body)> Posts { get; } = [];

    public AppCamera WithApp(string name, string status = "Running", string version = "1.0.0")
    {
        Apps.Add((name, version, status));
        return this;
    }

    private HttpResponseMessage? Answer(HttpRequestMessage request, string? body)
    {
        var pathAndQuery = request.RequestUri!.IsAbsoluteUri ? request.RequestUri.PathAndQuery : request.RequestUri.OriginalString;
        var path = pathAndQuery.Split('?')[0];
        if (request.Method == HttpMethod.Post)
        {
            Posts.Add((path, body ?? ""));
        }

        if (path == DeviceVpn.AppListPath)
        {
            var xml = new StringBuilder("<reply result=\"ok\">");
            foreach (var (name, version, status) in Apps)
            {
                xml.Append(CultureInfoInvariant($"<application Name=\"{name}\" NiceName=\"{name}\" Vendor=\"Mo3he\" Version=\"{version}\" Status=\"{status}\" />"));
            }

            return Ok(xml.Append("</reply>").ToString());
        }

        if (path == DeviceVpn.AppControlPath)
        {
            var package = pathAndQuery.Split("package=")[1];
            var index = Apps.FindIndex(a => a.Name == package);
            Apps[index] = Apps[index] with { Status = "Running" };
            return Ok("OK");
        }

        if (path == DeviceVpn.ParamPath)
        {
            return null; // the FakeCamera's parameters
        }

        return Files.TryGetValue(path, out var text) ? Ok(text) : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not found") };
    }

    private static string CultureInfoInvariant(FormattableString s) => FormattableString.Invariant(s);

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
}
