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

    /// <summary>
    /// The newest version is the commit about to be made, or the one just made.
    /// </summary>
    /// <remarks>
    /// The release number is the commit count, and the build refuses to publish
    /// a commit the changelog does not name. Every other test here reads the
    /// file alone, so all of them passed while a commit went out with no entry
    /// of its own - a tidy-up commit nobody thought of as a release - and the
    /// build died on it with "Program/Changelog.md has no entry for release
    /// 225". Twice. Two numbers are allowed because this runs on both sides of
    /// a commit: before it the file is one ahead of the count, after it they
    /// are equal.
    /// </remarks>
    [Fact]
    public void TheNewestVersion_IsTheCommitItIsWrittenFor()
    {
        if (!TryCountCommits(out var commits)) return;

        var newest = ChangelogService.Load()[0].Version;
        Assert.True(
            newest == commits || newest == commits + 1,
            $"the changelog's newest version is {newest} and main has {commits} commits; " +
            $"a commit needs its own '## {commits + 1}' section before it is made, " +
            "or the build will refuse to publish it");
    }

    /// <summary>How many commits lead here, when this is a git checkout at all.</summary>
    private static bool TryCountCommits(out int commits)
    {
        commits = 0;
        try
        {
            using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-list --count HEAD",
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (git is null) return false;
            var output = git.StandardOutput.ReadToEnd();
            if (!git.WaitForExit(15_000)) return false;
            return git.ExitCode == 0 && int.TryParse(output.Trim(), out commits);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No git here - a source drop rather than a checkout. The build
            // that publishes is a checkout and does the same sum.
            return false;
        }
    }

    /// <summary>
    /// A version says two short sentences and stops. This is the window a
    /// player opens to see what changed, not the commit that changed it: the
    /// two most visible things go here and the rest stays in the history.
    /// </summary>
    [Fact]
    public void EveryVersion_IsTwoShortSentences()
    {
        const int limit = 240;
        foreach (var entry in ChangelogService.Load())
        {
            Assert.True(
                entry.Text.Length <= limit,
                $"version {entry.Version} is {entry.Text.Length} characters; {limit} is the most");
            // A full stop ends a sentence when the text ends there or a space
            // follows it; the one inside a file name like `.log.gz` does not.
            var sentences = Enumerable.Range(0, entry.Text.Length)
                .Count(index =>
                    entry.Text[index] is '.' or '!' or '?' &&
                    (index == entry.Text.Length - 1 || char.IsWhiteSpace(entry.Text[index + 1])));
            Assert.True(
                sentences <= 2,
                $"version {entry.Version} says {sentences} sentences; two is the most");
        }
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
