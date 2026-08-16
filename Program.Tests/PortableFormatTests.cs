using System.Reflection;
using System.Text.RegularExpressions;

namespace Minecraft.Tests;

/// <summary>
/// Every file the launcher writes and everything it says to another launcher
/// carries one version, and releasing a change means incrementing one constant.
/// These cases exist so the next person does not reintroduce a per-file number
/// and a ladder of special cases to go with it.
/// </summary>
public sealed class PortableFormatTests
{
    /// <summary>Nothing declares its own version behind the shared one's back.</summary>
    [Theory]
    [InlineData(typeof(WorldMetadataService), "CurrentSchemaVersion")]
    [InlineData(typeof(SettingsService), "CurrentSchemaVersion")]
    [InlineData(typeof(WaypointStoreService), "SchemaVersion")]
    [InlineData(typeof(WorldTransferService), "ProtocolVersion")]
    [InlineData(typeof(WaypointSyncService), "ProtocolVersion")]
    [InlineData(typeof(SkinService), "ProtocolVersion")]
    [InlineData(typeof(SteamPresenceCodec), "ProtocolVersion")]
    [InlineData(typeof(BugReportManifest), "ProtocolVersion")]
    public void EveryVersionComesFromTheOneConstant(Type type, string member)
    {
        var value = (int)type.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

        Assert.True(
            value == PortableFormat.SchemaVersion || value == PortableFormat.ProtocolVersion,
            $"{type.Name}.{member} is {value}; it should be PortableFormat's version.");
    }

    /// <summary>
    /// The number starts above every per-file version that came before it, so a
    /// document written by an older build is simply older - never "newer" by
    /// accident, which would make this build refuse a file it can read.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    public void ADocumentFromAnyEarlierBuild_IsReadable(int schemaVersion) =>
        Assert.True(PortableFormat.CanRead(schemaVersion));

    [Fact]
    public void ADocumentFromANewerBuild_IsRefusedWithSomethingAPlayerCanActOn()
    {
        var future = PortableFormat.SchemaVersion + 1;

        Assert.False(PortableFormat.CanRead(future));
        Assert.False(PortableFormat.CanSpeak(PortableFormat.ProtocolVersion + 1));
        Assert.Contains("Обновите лаунчер", PortableFormat.DescribeUnreadable("Мир", future), StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionlessOrDamagedDocument_IsNotSilentlyAccepted()
    {
        Assert.False(PortableFormat.CanRead(0));
        Assert.False(PortableFormat.CanRead(-1));
        Assert.False(PortableFormat.CanSpeak(0));
    }

    /// <summary>
    /// The two numbers move together on every release, because the launcher
    /// that writes a file is the launcher that speaks the protocol.
    /// </summary>
    [Fact]
    public void TheSchemaAndProtocolVersionsAgree() =>
        Assert.Equal(PortableFormat.SchemaVersion, PortableFormat.ProtocolVersion);

    /// <summary>
    /// A literal version number in a service is how the per-file ladder grew
    /// last time. The pack manifest keeps its own, because pack authors write
    /// it and should not track launcher releases.
    /// </summary>
    [Fact]
    public void NoServiceDeclaresALiteralVersionOfItsOwn()
    {
        var pattern = new Regex(
            @"const\s+int\s+\w*(Schema|Protocol)Version\s*=\s*\d+",
            RegexOptions.CultureInvariant);
        // portable-pack.json is written by pack authors, and update.json is read
        // by launchers older than the one that wrote it; neither can follow this
        // build's version.
        var allowed = new[] { "PortableFormat.cs", "PackManifestService.cs", "UpdateService.cs" };

        var offenders = EnumerateSourceFiles()
            .Where(file => !allowed.Contains(Path.GetFileName(file), StringComparer.Ordinal))
            .Where(file => pattern.IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These declare their own version instead of PortableFormat's: {string.Join(", ", offenders)}");
    }

    private static IEnumerable<string> EnumerateSourceFiles()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Program", "Minecraft.csproj");
            if (File.Exists(candidate))
            {
                return Directory
                    .EnumerateFiles(Path.GetDirectoryName(candidate)!, "*.cs", SearchOption.AllDirectories)
                    .Where(file =>
                        !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal) &&
                        !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal));
            }
            current = current.Parent;
        }
        throw new FileNotFoundException("Program/Minecraft.csproj was not found.");
    }
}
