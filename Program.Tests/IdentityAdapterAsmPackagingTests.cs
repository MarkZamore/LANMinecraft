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

    [Fact]
    public void BuildScript_PinsTheVendoredAsmAndTargetsRelease21()
    {
        var script = File.ReadAllText(
            Path.Combine(
                FindRepositoryDirectory("Program", "IdentityAdapters", "Common"),
                "Build-IdentityAdapter.ps1"));

        // --release (not -source/-target) keeps a JDK 25 javac warning-free.
        Assert.Contains("--release 21", script, StringComparison.Ordinal);
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
