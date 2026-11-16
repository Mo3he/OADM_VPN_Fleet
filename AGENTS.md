# VPN Fleet: rules for AI assistants

This folder is an OADM plugin, made from the template `oadm-page`: a core plugin with its own page in the navigation rail: the server part (VpnFleet) and the page (VpnFleet.Client). Tests in VpnFleet.Tests.
Plugin id `mo3he.vpn-fleet`: it names the folder, the settings and the permissions. Never change it after a release.

OADM is the open alternative to AXIS Device Manager. Users are installers who know Axis devices.

## Build, test, run

```
dotnet tool restore          # the oadm tool of this SDK version
dotnet build                 # the plugin folder .oadm/plugins/mo3he.vpn-fleet/
dotnet test
dotnet oadm dev start        # an OADM server with this plugin, restarted after every build
dotnet oadm dev client       # in a second terminal: the client against it
dotnet oadm pack             # the .oadmplugin file for users
```

Run `dotnet test` after every step. Never automate the real desktop (no clicks, no screen captures).

## Guides

- Develop with the .NET SDK: https://oadm.app/dev/plugin-sdk/
- Tutorial of this kind of plugin: https://oadm.app/dev/tutorial-page/
- Plugin basics: https://oadm.app/dev/plugins/, devices and VAPIX: https://oadm.app/dev/devices/
- UI kit: https://oadm.app/dev/ui/, testing: https://oadm.app/dev/testing/
- Device groups: https://oadm.app/dev/device-groups/, SDK versions: https://oadm.app/dev/compatibility/
- SDK reference (every type and member): https://oadm.app/api/

## Device safety (HARD RULE)

A plugin that changes a device must never risk breaking it.

1. Declare the VAPIX APIs and minimum versions the plugin needs (`DeviceApi`). `CanRun` checks them against the
   cached `device.Apis`. `ExecuteAsync` reads a fresh `ctx.Vapix.GetApiListAsync()` and calls `Require(...)`
   before the first write. A different major version is a different API. An unsupported device fails with
   `DeviceNotCompatibleException` ("Nothing was changed").
2. Validate all input (addresses, masks, passwords, files) before the first write. Use the device's own dry run or
   validation where the API has one.
3. No lock-out: never remove or demote the account OADM uses. Settings that can make the device unreachable need a
   clear warning in the dialog.
4. Secrets: payloads stay in memory. Never persist or log passwords or keys.
5. `ITaskPluginQuery` is read only. Dialogs read the current state through `ITaskDialogContext.QueryAsync`.
6. Use `ReportWarning` for Done with warnings and `Log` for the task's log.
7. Tests that write to a real device are opt-in only (`[Trait("Category", "HardwareWrite")]`). Never run them
   unless the developer asks, and only against a test device.

## UI texts

- Short and plain, for installers who come from ADM. Subtitles: one short sentence or none.
- No protocol, API or internal names in the UI. Say what the user can do.
- Tooltips only when they add something, at most about 8 words, never the label again.
- Button and menu labels never end with "...". Step names: imperative with the article ("Wait for the device to
  come back").
- No filler ("easily", "simply", "please note"). "Optional" once per field. No "(s)" plurals.

## Client part

- Reuse the shared controls of `Oadm.Sdk.Client.Controls` (XAML prefix `ui`) and the OADM theme. No local font
  sizes, colours or paddings.
- Validation: forms derive from `ValidatingViewModel` with rules on its `FormValidator`; every field is a
  `ui:FormField`. The error shows directly below its field, only after the field was edited, left or a submit was
  tried. Submit buttons stay disabled while an error exists, with the reason as tooltip.
- Loading: anything that can take longer than 1 s shows it from the first moment (`ui:LoadingOverlay` for a
  dialog or page that waits for a read, `ui:ProgressRow` or `ui:ProgressRing` for a list that reloads). The window
  never freezes.
- Buttons (Fluent): toolbars and card headers use the flat `ui:ToolbarButton`. Icon-only buttons always have a
  tooltip. Only the one main action of a dialog or form is filled (`Button.primary`); `Button.secondary` for the
  others. Destructive confirmations are red (`Button.danger`).
- MVVM with CommunityToolkit (`[ObservableProperty]`, `[RelayCommand]`), no logic in code-behind. View models are
  testable without Avalonia.

## Scale

OADM manages up to 100,000 devices.

- A page that reads or changes devices starts from the user's choice (`ui:PageDeviceChoice` first in its toolbar;
  one device: `DevicePickerWindow.ShowOneAsync`), never from every device by itself.
- Lists of devices or results are virtualized (DataGrid, virtualizing ListBox), never one control per item. Bind
  them to `RangeObservableCollection` and change them in one step (`ReplaceAll`, `AddRange`).
- Index by id (`Dictionary`, `HashSet`); no `List.Contains` or `FirstOrDefault` over all rows inside a loop.
- Server calls for many devices are batched; per-device work has bounded parallelism.

## Code and tests

- Async all the way, a `CancellationToken` on every I/O method, no `.Result` or `.Wait()`.
- Reach devices only through `IVapixClient` and the host's services, never your own sockets or HTTP handlers, so
  devices at a site work too.
- Every non-trivial class has tests with the fakes of `Oadm.Sdk.Testing` (`FakeCamera`, `FakeDevice`,
  `FakeTaskContext`, `FakeActionContext`, `FakeCoreContext`, `StepRun`). No network in unit tests.
- Tests never flake: wait for other threads with `Eventually.TrueAsync(condition, "what")`, never a fixed
  `Task.Delay`. Time in the code comes from a `TimeProvider`; tests pass a `FakeTimeProvider` (package
  Microsoft.Extensions.TimeProvider.Testing) and advance it. Unique temp folders and ports per test.
- Tests check behaviour and content, never pixel positions or sizes.
