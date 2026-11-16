using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

using Oadm.Sdk.Client.Controls;

namespace VpnFleet.Client;

public sealed partial class VpnFleetView : UserControl
{
    public VpnFleetView() => AvaloniaXamlLoader.Load(this);

    // The pickers need the window: the view hands the view models a way to open them.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not VpnFleetViewModel vm)
        {
            return;
        }

        vm.PickDevices = async () => TopLevel.GetTopLevel(this) is Window owner && await vm.Devices.PickAsync(owner, "Choose devices");
        vm.Form.PickHub = async selected => TopLevel.GetTopLevel(this) is Window owner
            ? await DevicePickerWindow.ShowOneAsync(owner, vm.Devices.Candidates(), selected, heading: "Choose the hub camera")
            : null;
        vm.Form.PickProfile = PickProfileAsync;
    }

    private async Task<(string Name, string Text)?> PickProfileAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return null;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the VPN profile",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("OpenVPN profile") { Patterns = ["*.ovpn", "*.conf"] }],
        });
        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync();
        if (stream.CanSeek && stream.Length > ConnectRules.MaxProfileLength)
        {
            return (files[0].Name, new string('#', ConnectRules.MaxProfileLength + 1)); // the rule then shows "larger than 256 KB"
        }

        using var reader = new StreamReader(stream);
        return (files[0].Name, await reader.ReadToEndAsync());
    }

    // The host disposes the view model when the page or its window closes; that ends the watch.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as VpnFleetViewModel)?.Start();
    }
}
