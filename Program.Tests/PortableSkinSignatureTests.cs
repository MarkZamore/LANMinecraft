using System.IO;

namespace Minecraft.Tests;

/// <summary>
/// A skin nobody signed, on a Minecraft that demands a signature.
/// </summary>
/// <remarks>
/// Up to authlib 5.0.47 the game asks for a player's skin and says whether it
/// must be signed, and for another player it always must be. Only Mojang signs
/// skins, so a launcher skin never is: authlib answers "Signature is missing
/// from textures payload" and hands back nothing. The game then asks a second
/// time without the demand - but only for its own player, never for anyone
/// else. That is exactly what two players saw on All The Fabric 3: each their
/// own skin and a stranger in a default one across the table.
///
/// The demand is lowered on the one class this needs no mappings for, and only
/// for the profiles the launcher has a skin for, which by then carry that skin
/// and no other. The two halves live in different languages and build
/// separately, so what holds them together is that they agree on the name.
/// </remarks>
public sealed class PortableSkinSignatureTests
{
    [Fact]
    public void TheAdapter_LowersTheDemandForItsOwnSkinsOnly()
    {
        var profiles = ReadAdapterSource("Common", "PortableSkinProfiles.java");
        var transformer = ReadAdapterSource(
            "Minecraft-1.21.1-NeoForge", "PortableIdentityTransformer.java");

        // The lookup answers for the profiles the registry knows, and only
        // those.
        Assert.Contains("public static boolean isPortableSkin(Object profile)", profiles, StringComparison.Ordinal);
        Assert.Contains("loadEntries().containsKey(id)", profiles, StringComparison.Ordinal);

        // And the patch calls it by that name, with that shape.
        Assert.Contains("\"isPortableSkin\"", transformer, StringComparison.Ordinal);
        Assert.Contains("\"(Ljava/lang/Object;)Z\"", transformer, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only where authlib has the flag at all. From 6.0.52 the call is
    /// getPackedTextures(GameProfile) with nothing to lower, and a store into a
    /// local that does not exist is a game that will not start.
    /// </summary>
    [Fact]
    public void TheFlagIsLowered_OnlyWhereThereIsOneToLower()
    {
        var transformer = ReadAdapterSource(
            "Minecraft-1.21.1-NeoForge", "PortableIdentityTransformer.java");

        Assert.Contains(
            "method.desc.startsWith(\"(Lcom/mojang/authlib/GameProfile;Z)\")",
            transformer,
            StringComparison.Ordinal);
        Assert.Contains("Opcodes.ISTORE, 2", transformer, StringComparison.Ordinal);
    }

    private static string ReadAdapterSource(params string[] relativeParts)
    {
        var parts = new[] { "Program", "IdentityAdapters" }.Concat(relativeParts).ToArray();
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = parts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            current = current.Parent;
        }

        throw new FileNotFoundException($"Adapter source was not found: {Path.Combine(parts)}");
    }
}
