using System.Text.RegularExpressions;

namespace Minecraft.Tests;

/// <summary>
/// The Steam migration removed whole features - the VPN transport, the voice
/// channel, the LAN relay, the IP picker, the UUID.json migration. Deleted code
/// has a way of coming back as a helper someone reintroduces "just for now", so
/// this reads the source tree and fails if any of it reappears.
/// </summary>
public sealed class MigrationLeftoversTests
{
    /// <summary>Names that must not appear in the launcher's own source.</summary>
    public static TheoryData<string> RemovedIdentifiers =>
    [
        "VirtualNetworkService",
        "PeerDiscoveryService",
        "PeerRouteResolver",
        "NetworkEnvironmentSnapshot",
        "NetworkEndpointInfo",
        "NetworkSelectionPolicy",
        "NetworkAddressOption",
        "PeerAnnouncement",
        "KnownPeerCache",
        "LanRelayService",
        "LanRelayV2Tunnel",
        "LanAdvertisementService",
        "LanRelayPortStore",
        "PeerSupportCertificate",
        "PeerSupportTls",
        "PortableConnectionContext",
        "VoiceChannelService",
        "VoiceTransport",
        "VoiceSettingsWindow",
        "GlobalPttHotkeyService",
        "SteamIdentityStore",
        "IdentityConflictDialog",
        "EnsureBoundAsync",
        "MigrateLegacyPersonalData",
        "TryImportLegacyRuntime",
        "MigrateLegacyRegistry",
        "MigrateLegacyState",
        "TryBackUpBeforeMigration",
    ];

    [Theory]
    [MemberData(nameof(RemovedIdentifiers))]
    public void ARemovedFeature_HasNotComeBack(string identifier)
    {
        var offenders = EnumerateSourceFiles()
            .Where(file => File.ReadAllText(file).Contains(identifier, StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"'{identifier}' belongs to a removed feature but appears in: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// The launcher's own ports are gone with the sockets: Steam picks its own,
    /// and its virtual port is not a TCP port at all.
    /// </summary>
    [Theory]
    [InlineData("35655")]
    [InlineData("35657")]
    public void ARemovedPort_IsNotReferenced(string port)
    {
        var offenders = EnumerateSourceFiles()
            .Where(file => Regex.IsMatch(File.ReadAllText(file), $@"\b{port}\b"))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(offenders.Length == 0, $"Port {port} still appears in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NoSettingsKeyOfTheRemovedFeatures_Survives()
    {
        var properties = typeof(AppSettings).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(properties, name => name.Contains("Network", StringComparison.Ordinal));
        Assert.DoesNotContain(properties, name => name.Contains("Voice", StringComparison.Ordinal));
        Assert.DoesNotContain(properties, name => name.Contains("Lan", StringComparison.Ordinal));
    }

    /// <summary>
    /// Audio libraries were only ever there for the voice channel; a project
    /// reference to them means the feature is creeping back.
    /// </summary>
    [Fact]
    public void NoAudioDependency_Remains()
    {
        var project = File.ReadAllText(FindRepositoryFile("Program", "Minecraft.csproj"));

        Assert.DoesNotContain("NAudio", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Concentus", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Steamworks.NET", project, StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateSourceFiles()
    {
        var root = Path.GetDirectoryName(FindRepositoryFile("Program", "Minecraft.csproj"))!;
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                               StringComparison.Ordinal) &&
                           !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                               StringComparison.Ordinal));
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }
}
