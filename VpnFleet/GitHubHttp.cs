using System.Net;

namespace VpnFleet;

/// <summary>
/// The only HTTP of the plugin outside the host: HTTPS to GitHub for the releases of the apps and their packages.
/// No cookies; redirects are followed by <see cref="ReleaseCatalog"/> and only to GitHub's own hosts. Never a device.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The public releases on github.com, never a device.")]
public static class GitHubHttp
{
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>True for HTTPS to github.com, api.github.com or GitHub's file hosts (*.githubusercontent.com).</summary>
    public static bool IsGitHub(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && (uri.Host is "github.com" or "api.github.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
}
