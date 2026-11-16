using System.Globalization;
using System.Net;
using System.Text;

namespace VpnFleet;

/// <summary>An IPv4 tunnel network such as 10.66.0.1/24: the given address and its prefix.</summary>
public sealed record AddressPool(uint Address, int PrefixLength)
{
    public uint Mask => PrefixLength == 0 ? 0 : uint.MaxValue << (32 - PrefixLength);

    public uint Network => Address & Mask;

    public uint Broadcast => Network | ~Mask;

    /// <summary>"10.66.0.0/24".</summary>
    public string Subnet => WireGuardAddressing.Format(Network) + "/" + PrefixLength.ToString(CultureInfo.InvariantCulture);

    public bool Contains(uint address) => (address & Mask) == Network;

    /// <summary>An address of the network that a host may use (not the network or broadcast address).</summary>
    public bool IsHost(uint address) => Contains(address) && address != Network && address != Broadcast;

    /// <summary>"10.66.0.7/24": a camera's tunnel address as the WireGuard app takes it.</summary>
    public string WithPrefix(uint address) => WireGuardAddressing.Format(address) + "/" + PrefixLength.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A peer of the WireGuard Server app: its <c>Peers</c> parameter is "name,publicKey,address" records, ";" between.</summary>
public sealed record HubPeer(string Name, string PublicKey, string Address);

/// <summary>Tunnel addresses for the cameras and the peer list of the hub, without any device.</summary>
public static class WireGuardAddressing
{
    /// <summary>The prefix lengths the WireGuard Server app accepts.</summary>
    public const int MinPrefix = 8;
    public const int MaxPrefix = 30;

    /// <summary>An IPv4 network in CIDR form within /8../30; null otherwise.</summary>
    public static AddressPool? ParsePool(string? text)
    {
        var parts = (text ?? "").Trim().Split('/');
        if (parts.Length != 2
            || ParseAddress(parts[0]) is not { } address
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)
            || prefix is < MinPrefix or > MaxPrefix)
        {
            return null;
        }

        return new AddressPool(address, prefix);
    }

    /// <summary>A list of IPv4 networks, "," between ("10.66.0.0/24, 192.168.1.0/24"); null when one is not valid.</summary>
    public static IReadOnlyList<string>? ParseNetworks(string? text)
    {
        var list = new List<string>();
        foreach (var entry in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('/');
            if (parts.Length != 2
                || ParseAddress(parts[0]) is not { } address
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)
                || prefix > 32)
            {
                return null;
            }

            var mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
            list.Add(Format(address & mask) + "/" + prefix.ToString(CultureInfo.InvariantCulture));
        }

        return list;
    }

    public static uint? ParseAddress(string? text)
    {
        if (!IPAddress.TryParse((text ?? "").Trim(), out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || (text ?? "").Trim().Count(c => c == '.') != 3)
        {
            return null;
        }

        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static string Format(uint address) => string.Create(CultureInfo.InvariantCulture,
        $"{address >> 24}.{(address >> 16) & 255}.{(address >> 8) & 255}.{address & 255}");

    /// <summary>
    /// A tunnel address for every device: the one it had before when that is still free in the pool, else the lowest free
    /// one. <paramref name="reserved"/> are taken (the server, other peers). Throws when the pool is full.
    /// </summary>
    public static IReadOnlyDictionary<Guid, uint> Allocate(
        AddressPool pool,
        IReadOnlyList<Guid> devices,
        IReadOnlyDictionary<Guid, uint> previous,
        IEnumerable<uint> reserved)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(previous);
        var taken = reserved.ToHashSet();
        var result = new Dictionary<Guid, uint>();

        // Keep earlier addresses first, so a device never takes the address another device of this run had.
        foreach (var id in devices)
        {
            if (previous.TryGetValue(id, out var before) && pool.IsHost(before) && taken.Add(before))
            {
                result[id] = before;
            }
        }

        var next = pool.Network + 1;
        foreach (var id in devices.Where(d => !result.ContainsKey(d)))
        {
            while (next < pool.Broadcast && taken.Contains(next))
            {
                next++;
            }

            if (next >= pool.Broadcast)
            {
                throw new InvalidOperationException($"The tunnel network {pool.Subnet} has no free address for every device. Choose a larger network.");
            }

            result[id] = next;
            taken.Add(next);
        }

        return result;
    }

    /// <summary>The records of a <c>Peers</c> parameter, without the ones that cannot be read.</summary>
    public static IReadOnlyList<HubPeer> ParsePeers(string? peers) =>
        [.. (peers ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.Split(',', StringSplitOptions.TrimEntries))
            .Where(f => f.Length == 3)
            .Select(f => new HubPeer(f[0], f[1], f[2].Split('/')[0]))];

    /// <summary>
    /// The new <c>Peers</c> value: the current records, without those the new peers replace (same name, key or address),
    /// then the new peers. Records it cannot read are kept as they are.
    /// </summary>
    public static string MergePeers(string? current, IReadOnlyList<HubPeer> added)
    {
        ArgumentNullException.ThrowIfNull(added);
        var names = added.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = added.Select(p => p.PublicKey).ToHashSet(StringComparer.Ordinal);
        var addresses = added.Select(p => p.Address).ToHashSet(StringComparer.Ordinal);
        var kept = (current ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(record =>
            {
                var f = record.Split(',', StringSplitOptions.TrimEntries);
                return f.Length != 3 || !(names.Contains(f[0]) || keys.Contains(f[1]) || addresses.Contains(f[2].Split('/')[0]));
            });
        return string.Join(";", kept.Concat(added.Select(p => $"{p.Name},{p.PublicKey},{p.Address}")));
    }

    /// <summary>The peer name of a camera on the hub: its serial number, the one thing that never changes.</summary>
    public static string PeerName(string serial, string address)
    {
        var source = string.IsNullOrWhiteSpace(serial) ? "camera-" + address : serial;
        var name = new StringBuilder();
        foreach (var c in source.Trim())
        {
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }

        return name.ToString();
    }

    /// <summary>The [Peer] section of one camera for the configuration of another WireGuard server.</summary>
    public static string PeerSection(string comment, string publicKey, string address) =>
        $"[Peer]\n# {comment}\nPublicKey = {publicKey}\nAllowedIPs = {address}/32\n";
}
