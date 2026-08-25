using System.IO;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// NBT strings are Java's modified UTF-8, and the launcher used to read and
/// write them as ordinary UTF-8. The two agree on everything up to U+07FF - all
/// of Latin, all of Cyrillic - which is why every real file round-tripped byte
/// for byte and nothing ever looked wrong. They part company in two places: a
/// nul, which Java writes as C0 80, and everything above the basic plane, which
/// Java writes as the two halves of its surrogate pair, three bytes each. An
/// emoji in a world's name, in an item renamed at an anvil, on a page of a book
/// came back from .NET as replacement diamonds, and the launcher wrote the
/// diamonds to disk on the next launch - silently, unloggably, and for good,
/// since it rewrites level.dat and every player profile every time the game is
/// started.
/// </summary>
public sealed class NbtStringCodecTests
{
    /// <summary>
    /// What the game writes for an emoji, and what the launcher must write for
    /// the same emoji: six bytes, not the four of ordinary UTF-8.
    /// </summary>
    [Fact]
    public void AnEmoji_IsWrittenTheWayJavaWritesIt()
    {
        var ours = NbtWriter.EncodeModifiedUtf8("🙂");

        Assert.Equal(new byte[] { 0xED, 0xA0, 0xBD, 0xED, 0xB9, 0x82 }, ours);
        Assert.NotEqual(Encoding.UTF8.GetBytes("🙂"), ours);
    }

    /// <summary>And read back from those same bytes, whole.</summary>
    [Fact]
    public void AnEmojiTheGameWrote_ComesBackAsItself()
    {
        var asTheGameWroteIt = new byte[] { 0xED, 0xA0, 0xBD, 0xED, 0xB9, 0x82 };

        Assert.Equal("🙂", NbtReader.DecodeModifiedUtf8(asTheGameWroteIt));
    }

    /// <summary>
    /// A nul inside a string is C0 80 both ways - never the single zero byte
    /// that would end the string early for anything reading it as bytes.
    /// </summary>
    [Fact]
    public void ANulInsideAString_IsTwoBytes()
    {
        var bytes = NbtWriter.EncodeModifiedUtf8("a\0b");

        Assert.Equal(new byte[] { 0x61, 0xC0, 0x80, 0x62 }, bytes);
        Assert.Equal("a\0b", NbtReader.DecodeModifiedUtf8(bytes));
    }

    /// <summary>
    /// The text that actually fills these files is untouched by the change:
    /// Russian, Latin and the private-use glyphs mods put in their fonts are
    /// the same bytes in both encodings, which is why every file on disk read
    /// correctly before this and reads identically after.
    /// </summary>
    [Theory]
    [InlineData("Chebupeli")]
    [InlineData("Мой мир")]
    [InlineData("minecraft:player.block_break_speed")]
    [InlineData("")]
    [InlineData("")]
    public void EverydayText_IsByteForByteWhatItAlwaysWas(string text)
    {
        var ours = NbtWriter.EncodeModifiedUtf8(text);

        Assert.Equal(Encoding.UTF8.GetBytes(text), ours);
        Assert.Equal(text, NbtReader.DecodeModifiedUtf8(ours));
    }

    /// <summary>
    /// Four-byte sequences cannot come from the game - Java would refuse to
    /// read its own file - but a third-party editor writes them. They are read
    /// rather than rejected, and written back the way the game wants them, so a
    /// file that came in mangled goes out repaired.
    /// </summary>
    [Fact]
    public void AnEditorsPlainUtf8_IsReadAndThenRepaired()
    {
        var fromAnEditor = Encoding.UTF8.GetBytes("🙂");

        var text = NbtReader.DecodeModifiedUtf8(fromAnEditor);

        Assert.Equal("🙂", text);
        Assert.Equal(new byte[] { 0xED, 0xA0, 0xBD, 0xED, 0xB9, 0x82 }, NbtWriter.EncodeModifiedUtf8(text));
    }

    /// <summary>
    /// And the whole way through a file: a world's name with an emoji in it
    /// survives being written and read as a tag, which is what the launcher does
    /// to level.dat on every single launch.
    /// </summary>
    [Fact]
    public void AWorldNamedWithAnEmoji_SurvivesTheLauncherWritingIt()
    {
        var name = "Мир 🌍 и книга 📖";
        var root = new NbtCompoundTag();
        var data = new NbtCompoundTag();
        data.Set("LevelName", new NbtStringTag(name));
        root.Set("Data", data);
        var path = Path.Combine(Path.GetTempPath(), $"nbt-emoji-{Guid.NewGuid():N}.dat");
        try
        {
            new NbtFile(string.Empty, root).Write(path);

            var readBack = NbtFile.Read(path);

            Assert.Equal(name, readBack.Root.GetCompound("Data")!.GetString("LevelName"));
            var bytes = File.ReadAllBytes(path);
            Assert.DoesNotContain(Encoding.UTF8.GetBytes("🌍"), bytes.AsEnumerable().ToArray());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
