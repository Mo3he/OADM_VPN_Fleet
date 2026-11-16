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

    /// <summary>The bodies of upload.cgi requests.</summary>
    public List<string> Uploads { get; } = [];

    /// <summary>What an upload installs: app name and version.</summary>
    public (string Name, string Version)? Installs { get; set; }

    /// <summary>Answers upload.cgi with this text instead of installing.</summary>
    public string? UploadAnswer { get; set; }

    public AppCamera On(string architecture, string axisOs)
    {
        Info = Info with { Architecture = architecture, Version = axisOs };
        return this;
    }

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

        if (path == DeviceVpn.UploadPath)
        {
            Uploads.Add(body ?? "");
            if (UploadAnswer is { } answer)
            {
                return Ok(answer);
            }

            var (name, version) = Installs ?? throw new InvalidOperationException("Set Installs for an upload.");
            var index = Apps.FindIndex(a => a.Name == name);
            if (index < 0)
            {
                Apps.Add((name, version, "Stopped"));
            }
            else
            {
                Apps[index] = Apps[index] with { Version = version };
            }

            return Ok("OK");
        }

        return Files.TryGetValue(path, out var text) ? Ok(text) : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not found") };
    }

    private static string CultureInfoInvariant(FormattableString s) => FormattableString.Invariant(s);

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
}

/// <summary>
/// GitHub for the tests: the newest release of each app (<see cref="Release"/>) and its files. A download of
/// github.com redirects to objects.githubusercontent.com like the real one.
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public Dictionary<string, (string Tag, string[] Assets)> Releases { get; } = new(StringComparer.Ordinal);

    /// <summary>The bytes of every file, by name; the digest in the release is their SHA-256 unless <see cref="WrongDigest"/>.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public bool WrongDigest { get; set; }

    /// <summary>Where downloads redirect to; another host than GitHub's makes the test of a bad redirect.</summary>
    public string FileHost { get; set; } = "https://objects.githubusercontent.com";

    public List<string> Requests { get; } = [];

    public FakeGitHub Release(VpnApp app, string tag, params string[] assets)
    {
        Releases[app.Repository] = (tag, assets);
        foreach (var asset in assets)
        {
            Files.TryAdd(asset, Encoding.UTF8.GetBytes("EAP " + asset));
        }

        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        lock (Requests)
        {
            Requests.Add(uri.ToString());
        }

        if (uri.Host == "api.github.com")
        {
            var repo = uri.AbsolutePath.Split('/')[3];
            if (!Releases.TryGetValue(repo, out var release))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            }

            var assets = string.Join(",", release.Assets.Select(a =>
            {
                var sha = WrongDigest ? new string('0', 64) : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Files[a]));
                return $$"""{"name":"{{a}}","size":{{Files[a].Length}},"digest":"sha256:{{sha}}","browser_download_url":"https://github.com/Mo3he/{{repo}}/releases/download/{{release.Tag}}/{{a}}"}""";
            }));
            var json = $$"""{"tag_name":"{{release.Tag}}","html_url":"https://github.com/Mo3he/{{repo}}/releases/tag/{{release.Tag}}","published_at":"2026-11-13T08:45:04Z","assets":[{{assets}}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }

        var name = uri.AbsolutePath.Split('/')[^1];
        if (uri.Host == "github.com")
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(FileHost + "/files/" + name);
            return Task.FromResult(redirect);
        }

        return Task.FromResult(Files.TryGetValue(name, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>A started plugin with a fake GitHub, a fake clock and its own package folder.</summary>
internal static class TestPlugin
{
    public static async Task<(VpnFleetPlugin Plugin, FakeCoreContext Server, FakeGitHub GitHub, Microsoft.Extensions.Time.Testing.FakeTimeProvider Time)> StartAsync()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-11-16T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var github = new FakeGitHub();
        var server = new FakeCoreContext { DataDirectory = Path.Combine(Path.GetTempPath(), "vpnfleet-tests", Guid.NewGuid().ToString("N")) };
        var plugin = new VpnFleetPlugin(time, github);
        await plugin.StartAsync(server, CancellationToken.None);
        return (plugin, server, github, time);
    }
}
