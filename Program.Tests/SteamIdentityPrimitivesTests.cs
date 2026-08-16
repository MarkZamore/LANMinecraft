namespace Minecraft.Tests;

/// <summary>
/// The two values everything else keys on after the migration: the peer id
/// (SteamID64) and the Minecraft UUID a Steam account derives when it has no
/// legacy UUID to inherit. Both must be stable forever - a change in either
/// silently detaches players from their progress.
/// </summary>
public sealed class SteamIdentityPrimitivesTests
{
    [Theory]
    [InlineData("76561197960287930")]
    [InlineData("76561198000000000")]
    [InlineData(" 76561198000000001 ")]
    public void SteamId64_AcceptsIndividualAccounts(string text)
    {
        Assert.True(SteamId64.TryParse(text, out var steamId));
        Assert.True(steamId.IsValid);
        Assert.Equal(text.Trim(), steamId.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("76561197960265728")] // the base itself: account id 0
    [InlineData("103582791429521408")] // a group, not an individual account
    [InlineData("06c83c9e-980b-47d5-b7be-23d2bb649068")] // the VPN-era peer id
    [InlineData("+76561198000000001")]
    [InlineData("7656119800000000A")]
    [InlineData("../76561198000000001")]
    public void SteamId64_RejectsEverythingElse(string? text)
    {
        Assert.False(SteamId64.TryParse(text, out var steamId));
        Assert.False(steamId.IsValid);
        Assert.False(SteamId64.TryNormalize(text, out var canonical));
        Assert.Equal(string.Empty, canonical);
    }

    [Fact]
    public void SteamId64_CanonicalFormIsSafeAsAPathSegment()
    {
        Assert.True(SteamId64.TryNormalize("76561198000000001", out var canonical));
        Assert.Equal("76561198000000001", canonical);
        Assert.Equal(canonical, Path.GetFileName(canonical));
        Assert.DoesNotContain(canonical, character => Path.GetInvalidFileNameChars().Contains(character));
    }

    [Fact]
    public void SteamId64_ParseThrowsOnlyForInvalidInput()
    {
        Assert.Equal(76561198000000001UL, SteamId64.Parse("76561198000000001").Value);
        Assert.Throws<FormatException>(() => SteamId64.Parse("nope"));
    }

    [Fact]
    public void UuidV5_MatchesTheRfc4122Example()
    {
        // RFC 4122 / Python uuid.uuid5(NAMESPACE_DNS, "python.org").
        var dnsNamespace = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        Assert.Equal(
            new Guid("886313e1-3b8a-5372-9b90-0c9aee199e5d"),
            SteamIdentityDerivation.UuidV5(dnsNamespace, "python.org"));
    }

    [Theory]
    [InlineData(76561197960287930UL, "dfc933d4-fbda-5615-8d5f-b963ab3d5bc8")]
    [InlineData(76561198000000000UL, "659dd220-2e48-5a5f-a4de-a3fafb2f784b")]
    [InlineData(76561198000000001UL, "6ed27e4a-4a65-58a0-86d5-7e1c41f1701c")]
    public void DerivedUuid_IsFrozenPerSteamAccount(ulong steamId64, string expected)
    {
        Assert.True(SteamId64.TryFrom(steamId64, out var steamId));
        Assert.Equal(new Guid(expected), SteamIdentityDerivation.DeriveMinecraftUuid(steamId));
    }

    [Fact]
    public void DerivedUuid_IsAVersion5Rfc4122Uuid()
    {
        Assert.True(SteamId64.TryFrom(76561198000000001UL, out var steamId));
        var bytes = SteamIdentityDerivation.DeriveMinecraftUuid(steamId).ToByteArray();
        // Guid keeps its first three fields little-endian, so the version and
        // variant bytes are at 7 and 8 in that layout.
        Assert.Equal(0x50, bytes[7] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    [Fact]
    public void DerivedUuid_DiffersPerAccountAndRequiresAValidId()
    {
        Assert.True(SteamId64.TryFrom(76561198000000001UL, out var first));
        Assert.True(SteamId64.TryFrom(76561198000000002UL, out var second));
        Assert.NotEqual(
            SteamIdentityDerivation.DeriveMinecraftUuid(first),
            SteamIdentityDerivation.DeriveMinecraftUuid(second));
        Assert.Throws<ArgumentException>(
            () => SteamIdentityDerivation.DeriveMinecraftUuid(SteamId64.None));
    }

    [Fact]
    public void DerivationNamespaceAndNameFormat_AreFrozen()
    {
        Assert.Equal(new Guid("3b26fe30-fc0f-44d9-8a9e-08c3bde54df1"), SteamIdentityDerivation.Namespace);
        Assert.Equal("steam:76561198000000001", SteamIdentityDerivation.NameFor(76561198000000001UL));
    }
}
