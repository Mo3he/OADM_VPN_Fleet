using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace VpnFleet.Client;

/// <summary>
/// The rail icon of the plugin: a shield with "VPN" in it, in the style of the OADM icons (24 x 24, stroke 2, round
/// caps). OADM finds icons as resources "Icon.&lt;key&gt;" of the application, so the client part adds it there before the
/// rail shows the plugin. Without it OADM shows its plugin icon.
/// </summary>
public static class VpnIcon
{
    public const string Key = "vpnShield";

    public const string Path =
        "M12 1.3C9.6 2.9 6.3 3.8 2 4V12.5C2 17.4 5.7 20.9 12 22.9C18.3 20.9 22 17.4 22 12.5V4C17.7 3.8 14.4 2.9 12 1.3Z " +
        "M4.4 7.6L6 13.6L7.6 7.6 " +
        "M10.4 13.6V7.6H12.1C13.1 7.6 13.6 8.3 13.6 9.2C13.6 10.1 13.1 10.8 12.1 10.8H10.4 " +
        "M16.4 13.6V7.6L19.6 13.6V7.6";

    public static void Register()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Register);
            return;
        }

        if (!app.Resources.ContainsKey("Icon." + Key))
        {
            app.Resources["Icon." + Key] = StreamGeometry.Parse(Path);
        }
    }
}
