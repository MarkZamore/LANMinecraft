using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Minecraft.Tests;

/// <summary>
/// JDK 25 removed jdk.internal.org.objectweb.asm, so the bytecode adapter ships
/// the real ASM library instead. These tests pin that arrangement.
/// </summary>
public sealed class IdentityAdapterAsmPackagingTests
{
    private static readonly (string Name, long Length, string Sha256)[] PinnedAsmArtifacts =
    [
        ("asm-9.8.jar", 126_113, "876eab6a83daecad5ca67eb9fcabb063c97b5aeb8cf1fca7a989ecde17522051"),
        ("asm-tree-9.8.jar", 51_934, "14b7880cb7c85eed101e2710432fc3ffb83275532a6a894dc4c4095d49ad59f1")
    ];

    /// <summary>
    /// javac refuses a source file that begins with a byte order mark, and the
    /// only place it is ever noticed is the release build - the adapter is
    /// compiled there and nowhere else, so a mark added by an editor or a
    /// script takes the whole build down with an error about an illegal
    /// character. It happened once; this is why it cannot happen twice.
    /// </summary>
    [Fact]
    public void AdapterSources_DoNotStartWithAByteOrderMark()
    {
        var root = FindRepositoryDirectory("Program", "IdentityAdapters");
        var sources = Directory.EnumerateFiles(root, "*.java", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(sources);
        foreach (var path in sources)
        {
            var head = new byte[3];
            using (var stream = File.OpenRead(path))
            {
                if (stream.Read(head, 0, 3) < 3) continue;
            }
            Assert.False(
                head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF,
                $"{Path.GetFileName(path)} starts with a byte order mark, which javac will not read");
        }
    }

    [Fact]
    public void VendoredAsmArtifacts_MatchPinnedBytes()
    {
        var libraryRoot = FindRepositoryDirectory("Program", "IdentityAdapters", "Common", "lib");
        foreach (var (name, length, sha256) in PinnedAsmArtifacts)
        {
            var path = Path.Combine(libraryRoot, name);
            Assert.True(File.Exists(path), $"Vendored ASM library is missing: {name}");
            Assert.Equal(length, new FileInfo(path).Length);
            using var stream = File.OpenRead(path);
            Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
    }

    [Fact]
    public void VendoredAsmArtifacts_ContainEveryAsmTypeTheAdapterImports()
    {
        var libraryRoot = FindRepositoryDirectory("Program", "IdentityAdapters", "Common", "lib");
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, _, _) in PinnedAsmArtifacts)
        {
            using var archive = ZipFile.OpenRead(Path.Combine(libraryRoot, name));
            foreach (var entry in archive.Entries)
            {
                entries.Add(entry.FullName);
            }
        }

        var imported = EnumerateAdapterSources()
            .SelectMany(path => Regex.Matches(
                File.ReadAllText(path),
                @"^import (org\.objectweb\.asm\.[A-Za-z0-9_.$]+);",
                RegexOptions.Multiline))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(imported);
        foreach (var type in imported)
        {
            Assert.Contains(type.Replace('.', '/') + ".class", entries, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void AsmLicense_IsVendoredAndNamesTheCopyrightHolder()
    {
        var license = File.ReadAllText(
            Path.Combine(
                FindRepositoryDirectory("Program", "IdentityAdapters", "Common", "lib"),
                "LICENSE.asm.txt"));
        Assert.Contains("INRIA, France Telecom", license, StringComparison.Ordinal);
        foreach (var (name, _, sha256) in PinnedAsmArtifacts)
        {
            Assert.Contains(name, license, StringComparison.Ordinal);
            Assert.Contains(sha256, license, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoAdapterSource_ReferencesTheRemovedInternalAsm()
    {
        foreach (var path in EnumerateAdapterSources())
        {
            Assert.DoesNotContain(
                "jdk.internal.org.objectweb.asm",
                File.ReadAllText(path),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoLauncherSource_RequestsTheRemovedInternalAsmExport()
    {
        var programRoot = FindRepositoryDirectory("Program", "IdentityAdapters");
        var launcherRoot = Directory.GetParent(programRoot)!.FullName;
        foreach (var path in Directory.EnumerateFiles(launcherRoot, "*.cs", SearchOption.TopDirectoryOnly))
        {
            Assert.DoesNotContain(
                "jdk.internal.org.objectweb.asm",
                File.ReadAllText(path),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Only the hooks go on the bootstrap class path, never the whole agent.
    /// </summary>
    /// <remarks>
    /// The agent has to make its hook classes visible to transformed Minecraft
    /// classes, and it did that by putting its own jar on the bootstrap search
    /// path. That jar carries a copy of ASM, and on the bootstrap path that
    /// copy shadows whatever the loader brought - with no code source, because
    /// bootstrap classes have none. Fabric finds its own libraries by asking
    /// where their classes came from, so it got no answer and refused to start:
    /// "missing loader library ASM", before a single line of the game ran. It
    /// was invisible while the adapter only ever attached to NeoForge. A second
    /// jar holding the hooks alone costs nothing, because not one of them
    /// touches ASM.
    /// </remarks>
    [Fact]
    public void TheBootstrapPath_GetsTheHooksAlone_AndNeverAsm()
    {
        var common = FindRepositoryDirectory("Program", "IdentityAdapters", "Common");
        var script = File.ReadAllText(Path.Combine(common, "Build-IdentityAdapter.ps1"));
        var agent = File.ReadAllText(Path.Combine(common, "PortableIdentityAgent.java"));

        // The build makes the second jar, and it names every class the patched
        // game calls. A hook added without a line here is one the game cannot
        // see, which shows up as a NoClassDefFoundError inside Minecraft.
        Assert.Contains("portable-identity-hooks.jar", script, StringComparison.Ordinal);
        foreach (var hook in new[]
                 {
                     "PortableIdentityHooks", "PortableIdentityProfiles", "PortableIdentityReflection",
                     "PortableSkinProfiles", "PortableXaeroWaypointHooks"
                 })
        {
            Assert.Contains($"\"{hook}\"", script, StringComparison.Ordinal);
        }

        // The hooks LEAVE the agent jar rather than being copied out of it. A
        // second copy inside the agent jar is one Fabric finds through
        // parentClassLoader.getResource, and a class found there that it was
        // not told to expose is refused outright - "as it hasn't been exposed
        // to the game" - instead of being delegated. With the only copy on the
        // bootstrap path getResource answers null, which is the branch where
        // Fabric asks the platform loader and gets the class.
        Assert.Contains("Move-Item -LiteralPath $file.FullName", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item -LiteralPath $file.FullName", script, StringComparison.Ordinal);

        // And the agent puts that jar there, not the one it is running from.
        Assert.Contains("/portable-identity-hooks.jar", agent, StringComparison.Ordinal);
        Assert.Contains("appendToBootstrapClassLoaderSearch", agent, StringComparison.Ordinal);
        Assert.DoesNotContain("getCodeSource().getLocation().toURI()", agent, StringComparison.Ordinal);

        // None of the hooks may reach for ASM, or the split stops working.
        foreach (var hook in Directory.EnumerateFiles(
                     FindRepositoryDirectory("Program", "IdentityAdapters"),
                     "*Hooks.java",
                     SearchOption.AllDirectories)
                     .Concat(new[]
                     {
                         Path.Combine(common, "PortableSkinProfiles.java"),
                         Path.Combine(common, "PortableIdentityProfiles.java"),
                         Path.Combine(common, "PortableIdentityReflection.java"),
                     }))
        {
            Assert.DoesNotContain("org.objectweb.asm", File.ReadAllText(hook), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The adapter is built for the oldest Java a pack it reaches is started
    /// on, not for the newest.
    /// </summary>
    /// <remarks>
    /// It said 21, which was true while the adapter ran in one pack on 1.21.1.
    /// Then it started reaching every pack, and All The Fabric 3 would not
    /// start at all: 1.18.2 runs on Java 17, and a class file built for 21 does
    /// not load there - the launcher's own preflight died on
    /// UnsupportedClassVersionError and put it on screen as a dialog there was
    /// no way past. Java 8 is the real floor the launcher ships, and the agent
    /// cannot reach it yet: it implements the ClassFileTransformer.transform
    /// overload taking a Module, which arrived in Java 9.
    /// </remarks>
    [Fact]
    public void BuildScript_PinsTheVendoredAsm_AndBuildsForTheOldestJavaAPackRunsOn()
    {
        var script = File.ReadAllText(
            Path.Combine(
                FindRepositoryDirectory("Program", "IdentityAdapters", "Common"),
                "Build-IdentityAdapter.ps1"));

        // --release (not -source/-target) keeps a JDK 25 javac warning-free.
        Assert.Contains("--release 17", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--release 21", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-source 21", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--add-exports", script, StringComparison.Ordinal);
        foreach (var (name, length, sha256) in PinnedAsmArtifacts)
        {
            Assert.Contains(name, script, StringComparison.Ordinal);
            Assert.Contains(length.ToString(System.Globalization.CultureInfo.InvariantCulture), script, StringComparison.Ordinal);
            Assert.Contains(sha256, script, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> EnumerateAdapterSources() => Directory.EnumerateFiles(
        FindRepositoryDirectory("Program", "IdentityAdapters"),
        "*.java",
        SearchOption.AllDirectories);

    private static string FindRepositoryDirectory(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Repository directory was not found: {Path.Combine(relativeParts)}");
    }
}
