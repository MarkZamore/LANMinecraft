using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The version history the window shows is written by hand; these keep it
/// readable by the parser and honest about its shape.
/// </summary>
public sealed class ChangelogTests
{
    [Fact]
    public void EmbeddedChangelog_IsCompleteAndNewestFirst()
    {
        var entries = ChangelogService.Load();

        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.True(entry.Version >= 1, $"version {entry.Version}"));
        for (var index = 1; index < entries.Count; index++)
        {
            Assert.True(
                entries[index].Version < entries[index - 1].Version,
                $"{entries[index].Version} follows {entries[index - 1].Version}; the file lists newest first");
        }
        Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Text), $"version {entry.Version}"));
        // One version is one paragraph, and the paragraphs use a plain hyphen.
        Assert.All(entries, entry => Assert.DoesNotContain('\u2014', entry.Text));
        // History starts with the first public release and never skips a number.
        Assert.Equal(1, entries[^1].Version);
        Assert.Equal(entries[0].Version, entries.Count);
    }

    [Fact]
    public void Parse_ReadsHeadingsAndBullets()
    {
        const string sample = """
            # Что нового

            ## 3
            - Одна строка описания.

            ## 2
            - Одна строка.
            ## 1
            - Начало.
            """;

        var entries = ChangelogService.Parse(sample);

        Assert.Equal([3, 2, 1], entries.Select(entry => entry.Version).ToArray());
        Assert.Equal("Одна строка описания.", entries[0].Text);
        Assert.Equal("Начало.", entries[2].Text);
    }

    [Theory]
    [InlineData("- orphan bullet")]
    [InlineData("## 2\n## 1\n- a")]
    [InlineData("## 1\n- a\n## 2\n- b")]
    [InlineData("## 2\n- a\n## 2\n- b")]
    [InlineData("## 0\n- a")]
    [InlineData("## x\n- a")]
    [InlineData("## 1\nplain text")]
    [InlineData("## 1\n- ")]
    [InlineData("## 2\n- a\n- b\n## 1\n- c")]
    [InlineData("## 1\n- a \u2014 b")]
    public void Parse_RejectsMalformedInput(string sample)
    {
        Assert.Throws<FormatException>(() => ChangelogService.Parse(sample));
    }

    [Fact]
    public void Load_ReturnsNothingWhenTheResourceIsMissing()
    {
        var entries = ChangelogService.Load(typeof(ChangelogService).Assembly, "Minecraft.NoSuchChangelog.md");

        Assert.Empty(entries);
    }
}
