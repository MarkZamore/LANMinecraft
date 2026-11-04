using System.IO;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class LegacyIdentityRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "legacy-identity-registry-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void CanonicalNativeNamesRoundTripAndKeepEveryAccountDistinct()
    {
        foreach (var value in new[] { 76561197960265729UL, 76561198000000001UL, 76561202255233023UL })
        {
            Assert.True(SteamId64.TryFrom(value, out var steam));
            var name = LegacyPlayerIdentity.NativeName(steam);
            Assert.Equal(14, name.Length);
            Assert.True(LocalIdentityService.IsNameMinecraftAccepts(name));
            Assert.True(LegacyPlayerIdentity.TrySteamId(name, out var decoded));
            Assert.Equal(steam, decoded);
        }
        Assert.False(LegacyPlayerIdentity.TrySteamId("szzzzzzzzzzzzz", out _));
        Assert.False(LegacyPlayerIdentity.TrySteamId("s0000000000000", out _));
    }

    [Fact]
    public void SameDisplayNameRemainsTwoAccountsAndNicknameChangesDoNotChangeStorageKey()
    {
        var paths = new AppPaths(_root);
        var registry = new LegacyIdentityRegistryService(paths, new Logger(Path.Combine(_root, "log.txt")));
        var a = TestIdentity.CreateContext("SameName", 76561198000000001);
        var b = TestIdentity.CreateContext("SameName", 76561198000000002);
        registry.ObservePeer(new PeerViewModel { SteamId = b.SteamId64, PlayerName = b.IdentityName, MinecraftUuid = b.MinecraftUuid });
        var first = File.ReadAllLines(registry.Prepare(a));
        Assert.Equal(2, first.Length);
        a.IdentityName = "Renamed";
        var second = File.ReadAllLines(registry.Prepare(a));
        Assert.Equal(first.Select(line => line.Split('|')[1]), second.Select(line => line.Split('|')[1]));
        Assert.Contains(second, line => Encoding.UTF8.GetString(Convert.FromBase64String(line.Split('|')[2])) == "Renamed");
        Assert.All(second, line => Assert.Equal(4, line.Split('|').Length));
        Assert.False(File.Exists(paths.IdentityRegistryFile));
    }

    [Theory]
    [InlineData("Игрок")]
    [InlineData("Игрок|друг")]
    [InlineData("Old Name")]
    public void SavedUnicodeAndDelimiterNames_AreEncodedWithoutChangingTheOrdinaryRegistry(string name)
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(paths.Personal);
        File.WriteAllText(paths.IdentityRegistryFile, "ordinary registry remains unchanged");
        var registry = new LegacyIdentityRegistryService(paths, new Logger(Path.Combine(_root, "log.txt")));
        var identity = TestIdentity.CreateContext("Initial", 76561198000000001);
        identity.IdentityName = name;

        var line = Assert.Single(File.ReadAllLines(registry.Prepare(identity)));

        var fields = line.Split('|');
        Assert.Equal(4, fields.Length);
        Assert.Equal(name, Encoding.UTF8.GetString(Convert.FromBase64String(fields[2])));
        Assert.Equal("ordinary registry remains unchanged", File.ReadAllText(paths.IdentityRegistryFile));
        Assert.Equal(LegacyPlayerIdentity.NativeName(identity.SteamId64), fields[1]);
    }
}
