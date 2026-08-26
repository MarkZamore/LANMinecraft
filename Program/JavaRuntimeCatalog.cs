namespace Minecraft;

/// <summary>
/// The Java runtimes the launcher will install, one per feature release it can
/// be asked for.
///
/// Every field here was taken from the release itself rather than derived: the
/// sizes and checksums come from Adoptium's own metadata for that exact build,
/// and each archive root prefix was read out of the first entry of the zip,
/// because the name of the folder inside an archive is not something to guess
/// at when getting it wrong strips the install to nothing.
///
/// The version string beside each one is not the release name and not the file
/// name: it is what that runtime's own <c>release</c> file answers for
/// JAVA_VERSION, read out of each archive. That distinction is the whole
/// difference between a runtime that installs once and one that reinstalls for
/// ever, because an installed copy is recognised by comparing exactly that
/// string - and it is not guessable from anything else. The build called
/// jdk-21.0.12.1+1 reports "21.0.12.1", the one called jdk-21.0.12+8 reports
/// "21.0.12", and a release named jdk8u504-b01 reports "1.8.0_504".
/// </summary>
internal static class JavaRuntimeCatalog
{
    /// <summary>
    /// What a modern Minecraft is built for, and what a pack that says nothing
    /// and looks like nothing recognisable is given.
    /// </summary>
    public const int DefaultMajorVersion = 21;

    private static IReadOnlyList<Uri> UrisFor(string releaseName, string repository, string archiveFileName) =>
        Array.AsReadOnly<Uri>(
        [
            new Uri(
                $"https://github.com/adoptium/{repository}/releases/download/" +
                $"{Uri.EscapeDataString(releaseName)}/{archiveFileName}",
                UriKind.Absolute),
            // The same file by version rather than by release asset. Kept as a
            // second way in, not a different build: Adoptium serves this from
            // the release above.
            new Uri(
                $"https://api.adoptium.net/v3/binary/version/{Uri.EscapeDataString(releaseName)}" +
                "/windows/x64/jdk/hotspot/normal/eclipse",
                UriKind.Absolute)
        ]);

    /// <summary>Every runtime the launcher knows how to install, newest first.</summary>
    public static IReadOnlyList<JavaRuntimePin> Releases { get; } = Array.AsReadOnly<JavaRuntimePin>(
    [
        new(
            25,
            "temurin-25.0.4.1+1",
            "25.0.4.1",
            "java-25",
            "OpenJDK25U-jdk_x64_windows_hotspot_25.0.4.1_1.zip",
            "jdk-25.0.4.1+1/",
            UrisFor("jdk-25.0.4.1+1", "temurin25-binaries", "OpenJDK25U-jdk_x64_windows_hotspot_25.0.4.1_1.zip"),
            141_167_264,
            "00c847d804f4a78e9f04f2683faf14fed898535b177b7fc704486cb0284e9283",
            600L * 1024 * 1024,
            VerifyFlags: true),
        new(
            21,
            "temurin-21.0.12.1+1",
            "21.0.12.1",
            "java-21",
            "OpenJDK21U-jdk_x64_windows_hotspot_21.0.12.1_1.zip",
            "jdk-21.0.12.1+1/",
            UrisFor("jdk-21.0.12.1+1", "temurin21-binaries", "OpenJDK21U-jdk_x64_windows_hotspot_21.0.12.1_1.zip"),
            205_073_461,
            "f9d6e191ab098c0d416e7d588a24420a8621cd2f4720dab2459b8b7b2d2d8b4e",
            800L * 1024 * 1024,
            VerifyFlags: true),
        new(
            17,
            "temurin-17.0.20.1+1",
            "17.0.20.1",
            "java-17",
            "OpenJDK17U-jdk_x64_windows_hotspot_17.0.20.1_1.zip",
            "jdk-17.0.20.1+1/",
            UrisFor("jdk-17.0.20.1+1", "temurin17-binaries", "OpenJDK17U-jdk_x64_windows_hotspot_17.0.20.1_1.zip"),
            190_817_615,
            "e53a79c3c3d86865bd7e787903884331068e71321714ffd44f145785affc7cb0",
            700L * 1024 * 1024,
            VerifyFlags: true),
        new(
            8,
            "temurin-8u504-b01",
            "1.8.0_504",
            "java-8",
            "OpenJDK8U-jdk_x64_windows_hotspot_8u504b01.zip",
            "jdk8u504-b01/",
            UrisFor("jdk8u504-b01", "temurin8-binaries", "OpenJDK8U-jdk_x64_windows_hotspot_8u504b01.zip"),
            106_457_258,
            "ea43d46ede95b51e44a12c66711706cddc762e0a766c54bccea18954e902b2aa",
            400L * 1024 * 1024,
            // Java 8 predates every option the modern list carries, and the
            // probe would refuse a runtime that is doing nothing wrong.
            VerifyFlags: false)
    ]);

    /// <summary>The runtime for one feature release, or null if none is pinned.</summary>
    public static JavaRuntimePin? ForMajorVersion(int majorVersion) =>
        Releases.FirstOrDefault(release => release.MajorVersion == majorVersion);

    /// <summary>Every install folder a pinned runtime uses.</summary>
    public static IReadOnlyCollection<string> InstallDirectoryNames { get; } =
        Releases.Select(release => release.InstallDirectoryName).ToArray();

    /// <summary>Every archive folder a pinned runtime caches under.</summary>
    public static IReadOnlyCollection<string> CacheDirectoryNames { get; } =
        Releases.Select(release => release.RuntimeId.Replace('+', '_')).ToArray();
}
