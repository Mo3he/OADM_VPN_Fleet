# VPN Fleet for OADM

An [OADM](https://github.com/Cacsjep/oadm) plugin for the VPN apps of
[github.com/Mo3he](https://github.com/Mo3he): see the tunnel of every camera on one page and connect many cameras to a
VPN at once.

| VPN | App (package) | Status read from | Connect sets |
|---|---|---|---|
| Tailscale | `Tailscale_VPN` | `/local/Tailscale_VPN/status.json` | `AuthKey`, optional `CustomServer` (Headscale) |
| NetBird | `NetBird_VPN` | `/local/NetBird_VPN/api/status` | `SetupKey`, optional `ManagementURL` |
| ZeroTier | `ZeroTier_VPN` | `/local/ZeroTier_VPN/status.json` | `NetworkID` |
| OpenVPN | `OpenVPN_VPN` | `/local/OpenVPN_VPN/status.json` | the `.ovpn` profile, optional `Username` / `Password` |
| WireGuard | `wireguardconfig` | the app's lines in the system log | a key pair and tunnel address per camera, server, key, networks |

## The page

**VPN Fleet** in the navigation rail (group Security):

- **Choose devices** / **Refresh**: one row per VPN app per device with its status (Connected, Connecting, Not
  connected, Not set up, App stopped, No VPN app), the tunnel address and the app version. Devices are read 16 at a time.
- **Connect to a VPN**: choose the VPN and enter its key; every chosen device gets a task that saves the settings in its
  VPN app, starts the app and waits up to 90 s for the tunnel (Done with a warning when it does not come up). The app
  must be installed (OADM's Applications page installs it on many devices at once).
- **WireGuard peers**: the `[Peer]` sections of the cameras connected to another WireGuard server, for its
  configuration.

### WireGuard

- **A camera with WireGuard Server** ([Axis_Cam_WireGuard_Server](https://github.com/Mo3he/Axis_Cam_WireGuard_Server))
  as hub: the plugin reads the hub's public key, tunnel network, port and public address, gives each camera the next
  free tunnel address, makes its key pair, adds the cameras to the hub's `Peers` (other peers stay) and sets up the
  cameras. Connecting a camera again keeps its address and replaces its key.
- **Another WireGuard server**: enter its address, public key and tunnel address (e.g. `10.66.0.1/24`); the cameras
  get the next addresses. Copy the peers from **WireGuard peers** into the server's configuration.

Private keys are made on the OADM server and only go to their camera, inside the task (task payloads are never
stored). The plugin keeps each camera's public key and address, never a private key, auth key, setup key or password.

## Permissions

- **Show** (automatic): see the page and read devices.
- **Connect devices to a VPN** (`mo3he.vpn-fleet:connect`): change VPN settings. Off for Operators by default; give it
  per role on the Permissions page. Every connect is in the audit log, without secrets.

Users limited to a device group only see and change their devices.

## Install

Download `mo3he.vpn-fleet-<version>.oadmplugin` and unpack it into a folder `mo3he.vpn-fleet` in the plugin folder of
the server and of each client (`dotnet oadm folders` shows them), or from a checkout:

```
dotnet oadm deploy --installed
```

Needs OADM 5.1 or later.

## Develop

Needs the .NET 10 SDK.

```
dotnet tool restore          # the oadm tool of this SDK version
dotnet build                 # the plugin folder .oadm/plugins/mo3he.vpn-fleet/
dotnet test
dotnet oadm dev start        # an OADM server with this plugin, restarted after every build
dotnet oadm dev client       # in a second terminal: the client against it
dotnet oadm pack             # the .oadmplugin file
```

| Folder | Content |
|---|---|
| `VpnFleet/` | Server part: the page methods (`VpnFleetPlugin`), the task (`ConnectTaskPlugin`), device requests (`DeviceVpn`), status parsers, WireGuard keys (X25519, RFC 7748) and addressing |
| `VpnFleet.Client/` | The page (Avalonia, MVVM) |
| `VpnFleet.Tests/` | Unit tests with OADM's fakes, including the device-group canary |

Rules for changes, also for AI assistants: [AGENTS.md](AGENTS.md).
