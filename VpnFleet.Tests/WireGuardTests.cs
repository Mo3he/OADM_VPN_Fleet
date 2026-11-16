namespace VpnFleet.Tests;

public sealed class WireGuardTests
{
    [Fact]
    public void X25519_matches_the_test_vectors_of_RFC_7748()
    {
        // Section 5.2, first vector.
        var scalar = Convert.FromHexString("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4");
        var u = Convert.FromHexString("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c");
        Assert.Equal("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552", Convert.ToHexStringLower(WireGuardKeys.X25519(scalar, u)));

        // Section 6.1: Alice's and Bob's public keys.
        Assert.Equal(
            Convert.ToBase64String(Convert.FromHexString("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a")),
            WireGuardKeys.PublicKeyOf(Convert.ToBase64String(Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"))));
        Assert.Equal(
            Convert.ToBase64String(Convert.FromHexString("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f")),
            WireGuardKeys.PublicKeyOf(Convert.ToBase64String(Convert.FromHexString("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb"))));
    }

    [Fact]
    public void Generated_keys_are_a_pair_and_new_every_time()
    {
        var a = WireGuardKeys.Generate();
        var b = WireGuardKeys.Generate();

        Assert.True(WireGuardKeys.IsKey(a.PrivateKey));
        Assert.True(WireGuardKeys.IsKey(a.PublicKey));
        Assert.Equal(a.PublicKey, WireGuardKeys.PublicKeyOf(a.PrivateKey));
        Assert.NotEqual(a.PrivateKey, b.PrivateKey);
    }

    [Theory]
    [InlineData("10.66.0.1/24", "10.66.0.0/24")]
    [InlineData("172.16.5.9/16", "172.16.0.0/16")]
    [InlineData("10.0.0.0/30", "10.0.0.0/30")]
    public void A_pool_is_an_IPv4_network_from_8_to_30(string text, string subnet) =>
        Assert.Equal(subnet, WireGuardAddressing.ParsePool(text)!.Subnet);

    [Theory]
    [InlineData("10.66.0.1")]
    [InlineData("10.66.0.1/31")]
    [InlineData("10.66.0.1/7")]
    [InlineData("fd00::1/64")]
    [InlineData("10.66.1/24")]
    public void Anything_else_is_not_a_pool(string text) => Assert.Null(WireGuardAddressing.ParsePool(text));

    [Fact]
    public void Devices_keep_their_address_and_new_ones_get_the_lowest_free()
    {
        var pool = WireGuardAddressing.ParsePool("10.66.0.1/24")!;
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var previous = new Dictionary<Guid, uint> { [b] = WireGuardAddressing.ParseAddress("10.66.0.2")!.Value };
        var reserved = new[] { pool.Address, WireGuardAddressing.ParseAddress("10.66.0.3")!.Value };

        var result = WireGuardAddressing.Allocate(pool, [a, b, c], previous, reserved);

        Assert.Equal("10.66.0.2", WireGuardAddressing.Format(result[b]));
        Assert.Equal("10.66.0.4", WireGuardAddressing.Format(result[a]));
        Assert.Equal("10.66.0.5", WireGuardAddressing.Format(result[c]));
        Assert.Equal("10.66.0.4/24", pool.WithPrefix(result[a]));
    }

    [Fact]
    public void A_full_pool_is_refused()
    {
        var pool = WireGuardAddressing.ParsePool("10.66.0.1/30")!; // hosts .1 and .2; .1 is the server
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WireGuardAddressing.Allocate(pool, [Guid.NewGuid(), Guid.NewGuid()], new Dictionary<Guid, uint>(), [pool.Address]));
        Assert.Contains("10.66.0.0/30", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void New_peers_replace_their_old_records_and_keep_the_others()
    {
        var key1 = WireGuardKeys.Generate().PublicKey;
        var key2 = WireGuardKeys.Generate().PublicKey;
        var key3 = WireGuardKeys.Generate().PublicKey;
        var current = $"laptop,{key1},10.66.0.2;B8A44F631339,{key2},10.66.0.3/32;broken record";

        var merged = WireGuardAddressing.MergePeers(current, [new HubPeer("B8A44F631339", key3, "10.66.0.3")]);

        Assert.Equal($"laptop,{key1},10.66.0.2;broken record;B8A44F631339,{key3},10.66.0.3", merged);
        Assert.Equal(2, WireGuardAddressing.ParsePeers(merged).Count);
    }

    [Fact]
    public void A_peer_name_never_breaks_the_peer_list() =>
        Assert.Equal("camera-10.0.0.48", WireGuardAddressing.PeerName("", "10.0.0.48"));
}
