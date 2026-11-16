using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace VpnFleet;

/// <summary>A file of a release.</summary>
public sealed record ReleaseAsset(string Name, long Size, string Url, string? Sha256);

/// <summary>The newest release of an app on GitHub.</summary>
public sealed record AppRelease(VpnKind Kind, string Version, string PageUrl, DateTimeOffset? Published, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// The newest releases of the apps from github.com/Mo3he (kept 30 minutes, a failure 5 minutes, so the 60 requests an
/// hour GitHub allows without a login are never used up) and their .eap files, downloaded once into the plugin's folder
/// and checked against the SHA-256 GitHub publishes.
/// </summary>
public sealed class ReleaseCatalog : IDisposable
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan KeepFailureFor = TimeSpan.FromMinutes(5);

    /// <summary>The largest package the plugin downloads.</summary>
    public const long MaxPackageBytes = 200L * 1024 * 1024;

    /// <summary>AXIS OS before this version runs ACAP 3 packages only (armv7hf).</summary>
    public const string NativeMinOs = "10.12";

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Func<string> _folder;
    private readonly Lock _gate = new();
    private readonly Dictionary<VpnKind, (DateTimeOffset At, AppRelease? Release, string? Error)> _cache = [];
    private readonly Dictionary<string, SemaphoreSlim> _downloads = new(StringComparer.Ordinal);

    public ReleaseCatalog(HttpMessageHandler handler, TimeProvider time, Func<string> folder)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OADM-VpnFleet", "1.0"));
        _time = time;
        _folder = folder;
    }

    /// <summary>The newest release of every app; an app GitHub did not answer for comes with its error.</summary>
    public async Task<IReadOnlyList<ReleaseInfo>> GetAllAsync(bool checkNow, CancellationToken ct)
    {
        var list = new List<ReleaseInfo>();
        foreach (var app in VpnApps.All)
        {
            var (release, error) = await GetAsync(app.Kind, checkNow, ct).ConfigureAwait(false);
            list.Add(new ReleaseInfo(app.Kind, release?.Version, release?.PageUrl, release?.Published, error));
        }

        return list;
    }

    public async Task<(AppRelease? Release, string? Error)> GetAsync(VpnKind kind, bool checkNow, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!checkNow && _cache.TryGetValue(kind, out var hit) && now - hit.At < (hit.Release is null ? KeepFailureFor : KeepFor))
            {
                return (hit.Release, hit.Error);
            }
        }

        var app = VpnApps.Of(kind);
        AppRelease? release = null;
        string? error = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{VpnApps.Owner}/{app.Repository}/releases/latest");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                error = (int)response.StatusCode == 403 ? "GitHub limits how often it can be asked. Try again later." : $"GitHub answered HTTP {(int)response.StatusCode}.";
            }
            else
            {
                release = ParseRelease(app, body) ?? throw new JsonException();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            error = ex is JsonException ? "GitHub's answer could not be read." : "GitHub could not be reached from the OADM server: " + ex.Message;
        }

        lock (_gate)
        {
            _cache[kind] = (now, release, error);
        }

        return (release, error);
    }

    /// <summary>A release of the GitHub API; null when it is not one. Only files of the app's own repository count.</summary>
    public static AppRelease? ParseRelease(VpnApp app, string json)
    {
        ArgumentNullException.ThrowIfNull(app);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var downloads = $"https://github.com/{VpnApps.Owner}/{app.Repository}/releases/download/";
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (name is null || url is null || !name.EndsWith(".eap", StringComparison.OrdinalIgnoreCase)
                    || !url.StartsWith(downloads, StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
                {
                    continue;
                }

                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                assets.Add(new ReleaseAsset(name, size, url, sha));
            }
        }

        var page = root.TryGetProperty("html_url", out var h) && h.GetString() is { } html && html.StartsWith("https://github.com/", StringComparison.Ordinal) ? html : null;
        var published = root.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var at) ? at : (DateTimeOffset?)null;
        return new AppRelease(app.Kind, AppVersions.FromTag(tag.GetString()!), page ?? $"https://github.com/{VpnApps.Owner}/{app.Repository}/releases", published, assets);
    }

    /// <summary>
    /// The package for a device: the signed build of its architecture; on armv7hf with AXIS OS before 10.12 the ACAP 3
    /// build. Null with the reason when the release has none for the device.
    /// </summary>
    public static (ReleaseAsset? Asset, string? Reason) ChooseAsset(AppRelease release, string? architecture, string? axisOs)
    {
        ArgumentNullException.ThrowIfNull(release);
        var arch = (architecture ?? "").Trim().ToLowerInvariant();
        if (arch.Length == 0)
        {
            return (null, "The device did not tell its architecture.");
        }

        var old = AppVersions.Parse(axisOs).Count > 0 && AppVersions.Compare(axisOs, NativeMinOs) < 0;
        bool Is(ReleaseAsset a, string suffix) => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        if (old)
        {
            var acap3 = release.Assets.FirstOrDefault(a => Is(a, $"_{arch}_acap3.eap"));
            return acap3 is null
                ? (null, $"This release needs AXIS OS {NativeMinOs} or later (this device has {axisOs}).")
                : (acap3, null);
        }

        var native = release.Assets.Where(a => Is(a, $"_{arch}.eap")).ToList();
        var chosen = native.FirstOrDefault(a => a.Name.StartsWith("signed_", StringComparison.OrdinalIgnoreCase)) ?? native.FirstOrDefault();
        return chosen is null ? (null, $"The release has no package for {arch}.") : (chosen, null);
    }

    /// <summary>
    /// The package as a local file: downloaded once (one download per file at a time), its size and SHA-256 checked. A
    /// file that does not match is deleted, never installed.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseAsset asset, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Size > MaxPackageBytes)
        {
            throw new InvalidOperationException("The package is larger than 200 MB.");
        }

        var folder = Directory.CreateDirectory(_folder()).FullName;
        var path = Path.Combine(folder, Path.GetFileName(asset.Name));
        SemaphoreSlim gate;
        lock (_gate)
        {
            if (!_downloads.TryGetValue(path, out gate!))
            {
                _downloads[path] = gate = new SemaphoreSlim(1, 1);
            }
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && await MatchesAsync(path, asset, ct).ConfigureAwait(false))
            {
                progress?.Report(asset.Size);
                return path;
            }

            var part = path + ".part";
            using (var response = await GetFollowingGitHubAsync(new Uri(asset.Url), ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"The download from GitHub failed (HTTP {(int)response.StatusCode}).");
                }

                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = File.Create(part);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxPackageBytes)
                    {
                        throw new InvalidOperationException("The package is larger than 200 MB.");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report(total);
                }
            }

            if (!await MatchesAsync(part, asset, ct).ConfigureAwait(false))
            {
                File.Delete(part);
                throw new InvalidOperationException("The downloaded package does not match the release on GitHub. Nothing was installed.");
            }

            File.Move(part, path, overwrite: true);
            return path;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>GET with the redirects GitHub sends to its file hosts, never to another host (at most 5).</summary>
    private async Task<HttpResponseMessage> GetFollowingGitHubAsync(Uri uri, CancellationToken ct)
    {
        for (var hops = 0; hops < 5; hops++)
        {
            if (!GitHubHttp.IsGitHub(uri))
            {
                throw new HttpRequestException("The download left GitHub. Nothing was installed.");
            }

            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                response.Dispose();
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                continue;
            }

            return response;
        }

        throw new HttpRequestException("GitHub redirected the download too often.");
    }

    private static async Task<bool> MatchesAsync(string path, ReleaseAsset asset, CancellationToken ct)
    {
        if (asset.Size > 0 && new FileInfo(path).Length != asset.Size)
        {
            return false;
        }

        if (asset.Sha256 is null)
        {
            return true;
        }

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(hash), asset.Sha256, StringComparison.Ordinal);
    }

    public void Dispose() => _http.Dispose();
}
