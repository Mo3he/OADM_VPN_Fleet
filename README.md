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

**VPN Fleet** in the navigation rail (group Security, a shield with "VPN"):

- **Choose devices** / **Refresh**: one row per VPN app per device with its status (Connected, Connecting, Not
  connected, Not set up, App stopped, No VPN app), the tunnel address, the installed version and, when GitHub has a
  newer release, the update. Devices are read 16 at a time.
- **Click a row**: the panel of that camera only. All settings of its app with their names (secrets show whether they
  are set, never their value; left empty they keep it), **Save** for this camera, **Upgrade to x.y.z** and **What's
  new**. WireGuard shows the camera's public key and makes a new key pair; OpenVPN takes a new profile. A camera
  without a VPN app offers to install one.
- **Install or upgrade**: choose an app; every chosen camera is listed with a checkbox and what happens to it
  ("Upgrade 1.104.6 to 1.104.7", "Install Tailscale 1.104.7", "1.104.7, the newest"). Outdated cameras are ticked.
- **Set up many cameras**: the same VPN settings for every ticked camera; the list says which cameras have the app
  (ticked) and which do not (not ticked, the task would refuse them).
- **WireGuard peers**: the `[Peer]` sections of the cameras connected to another WireGuard server.

### Install and upgrade

The newest release of each app comes from `github.com/Mo3he/<repository>/releases/latest` (asked at most every 30
minutes). Each camera gets the package for its architecture and AXIS OS: the signed build (`signed_*_<arch>.eap`), on
armv7hf with AXIS OS before 10.12 the ACAP 3 build (`*_armv7hf_acap3.eap`); `_root` builds are never chosen. The OADM
server downloads each package once into its plugin folder, only from GitHub's hosts, checks the SHA-256 GitHub
publishes, then installs it with `upload.cgi` (4 cameras at a time), starts the app and checks the installed version.
Upgrades keep the app's settings; a camera that has the version already is not changed. The OADM server needs HTTPS
access to `api.github.com`, `github.com` and `*.githubusercontent.com`.

### WireGuard

- **A camera with WireGuard Server** ([Axis_Cam_WireGuard_Server](https://github.com/Mo3he/Axis_Cam_WireGuard_Server))
  as hub: the plugin reads the hub's public key, tunnel network, port and public address, gives each camera the next
  free tunnel address, makes its key pair, adds the cameras to the hub's `Peers` (other peers stay) and sets up the
  cameras. Connecting a camera again keeps its address and replaces its key; a new key saved in a camera's panel also
  goes to its hub.
- **Another WireGuard server**: enter its address, public key and tunnel address (e.g. `10.66.0.1/24`); the cameras
  get the next addresses. Copy the peers from **WireGuard peers** into the server's configuration.

Private keys are made on the OADM server (or in the camera panel) and only go to their camera. The plugin keeps each
camera's public key and address, never a private key, auth key, setup key or password.

## Permissions

- **Show** (automatic): see the page, read devices and settings (without secrets).
- **Change VPN settings** (`mo3he.vpn-fleet:connect`): save a camera's settings and set up many cameras.
- **Install and upgrade VPN apps** (`mo3he.vpn-fleet:install`).

Both are off for Operators by default; give them per role on the Permissions page. Every change is in the audit log,
without secrets. Users limited to a device group only see and change their devices.

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
| `VpnFleet/` | Server part: the page methods (`VpnFleetPlugin`), the tasks (`ConnectTaskPlugin`, `InstallTaskPlugin`), device requests (`DeviceVpn`), GitHub releases (`ReleaseCatalog`, `GitHubHttp`), settings (`AppSettings`), status parsers, WireGuard keys (X25519, RFC 7748) and addressing |
| `VpnFleet.Client/` | The page (Avalonia, MVVM), the camera panel, the checklists and the rail icon (`VpnIcon`) |
| `VpnFleet.Tests/` | Unit tests with OADM's fakes, including the device-group canary |

Rules for changes, also for AI assistants: [AGENTS.md](AGENTS.md).
