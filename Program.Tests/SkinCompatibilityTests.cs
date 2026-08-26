using System.IO;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// A skin that is a perfectly good file and the wrong file for the pack.
/// </summary>
/// <remarks>
/// While every pack was modern this could not happen. The launcher carries
/// packs back to Minecraft 1.7 now, and the square layout with its second
/// layer and its own left arm - and the slim model with it - arrived in 1.8.
/// Hand a square skin to anything older and it is read as though it were the
/// old 64x32: the character comes out wrong and nothing says why.
/// </remarks>
public sealed class SkinCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-skin-compat-{Guid.NewGuid():N}");

    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    // Everything from 1.8 reads a square skin.
    [InlineData("1.8", true)]
    [InlineData("1.12.2", true)]
    [InlineData("1.21.1", true)]
    [InlineData("26.2", true)]
    // And nothing before it does.
    [InlineData("1.7.10", false)]
    [InlineData("1.6.4", false)]
    // A version nobody could parse is taken as modern: not knowing is not a
    // reason to warn about a skin that is almost certainly fine.
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("latest", true)]
    public void TheSquareLayout_ArrivedInOneEight(string? version, bool modern) =>
        Assert.Equal(modern, SkinCompatibility.ReadsModernSkins(version));

    /// <summary>The rule for the file is said on every pack.</summary>
    [Fact]
    public void TheHint_AlwaysSaysWhatTheFileMustBe()
    {
        foreach (var version in new[] { "1.7.10", "1.21.1" })
        {
            var hint = SkinCompatibility.Describe(version, skinPath: null);
            Assert.Contains("PNG", hint, StringComparison.Ordinal);
            Assert.Contains("64x32", hint, StringComparison.Ordinal);
            Assert.Contains("МБ", hint, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// And an old pack is told about, by name, with the skin that is chosen
    /// measured rather than assumed.
    /// </summary>
    [Fact]
    public void AnOldPack_IsToldAboutWithTheSkinThatIsChosen()
    {
        Directory.CreateDirectory(_root);
        var square = WritePng("square.png", 64, 64);
        var legacy = WritePng("legacy.png", 64, 32);

        var modern = SkinCompatibility.Describe("1.21.1", square);
        Assert.DoesNotContain("покажется неправильно", modern, StringComparison.Ordinal);

        var old = SkinCompatibility.Describe("1.7.10", square);
        Assert.Contains("Minecraft 1.7.10", old, StringComparison.Ordinal);
        Assert.Contains("64x64", old, StringComparison.Ordinal);
        Assert.Contains("покажется неправильно", old, StringComparison.Ordinal);

        // The same old pack with a skin it can actually read says the rule and
        // stops there.
        var fitting = SkinCompatibility.Describe("1.7.10", legacy);
        Assert.Contains("только 64x32", fitting, StringComparison.Ordinal);
        Assert.DoesNotContain("покажется неправильно", fitting, StringComparison.Ordinal);
    }

    [Fact]
    public void ASkinThatIsNotThere_IsMeasuredAsNothing()
    {
        Assert.Null(SkinCompatibility.MeasureSkin(null));
        Assert.Null(SkinCompatibility.MeasureSkin(""));
        Assert.Null(SkinCompatibility.MeasureSkin(Path.Combine(_root, "absent.png")));
    }

    /// <summary>A PNG header is all this needs, so a header is all it writes.</summary>
    private string WritePng(string name, int width, int height)
    {
        var path = Path.Combine(_root, name);
        var bytes = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        "IHDR"u8.ToArray().CopyTo(bytes, 12);
        BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(width)).CopyTo(bytes, 16);
        BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(height)).CopyTo(bytes, 20);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
