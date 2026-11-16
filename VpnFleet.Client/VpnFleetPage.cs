using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace VpnFleet.Client;

/// <summary>Found by the client loader: the rail entry and its page.</summary>
public sealed class VpnFleetPage : ICorePluginPage
{
    public string PluginId => Fleet.PluginId;

    public string Title => "VPN Fleet";

    // Called for every view of the page (the rail page and each "Open in new window"), each with its own context.
    public Control CreateView(ICorePluginClientContext ctx) => new VpnFleetView { DataContext = new VpnFleetViewModel(ctx) };
}
