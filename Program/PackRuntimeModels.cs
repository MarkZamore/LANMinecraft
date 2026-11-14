using System.Net.Http;

namespace Minecraft;

public enum PackLoaderKind
{
    Vanilla,
    Forge,
    NeoForge,
    Fabric,
    Quilt,
    LegacyFabric,
    Native
}

internal static class PackLoaderNames
{
    public static string? DisplayName(PackLoaderKind kind) => kind switch
    {
        PackLoaderKind.NeoForge => "NeoForge",
        PackLoaderKind.Forge => "Forge",
        PackLoaderKind.Fabric => "Fabric",
        PackLoaderKind.LegacyFabric => "Legacy Fabric",
        PackLoaderKind.Quilt => "Quilt",
        _ => null
    };
}

public sealed record PackLoaderDescriptor(PackLoaderKind Type, string? Version);

public sealed record NativePackRuntimeDescriptor(string Executable, string Platform, string Adapter, string Version);

public sealed record PackRuntimeDescriptor(
    int SchemaVersion,
    string MinecraftVersion,
    PackLoaderDescriptor Loader,
    string ClientJar,
    string DescriptorHash)
{
    /// <summary>An explicit Java feature release requested by the pack.</summary>
    public int? JavaMajorVersion { get; init; }
    public bool PreserveWorldSettingsOnShare { get; init; }
    public NativePackRuntimeDescriptor? NativeRuntime { get; init; }
}

public enum RuntimePreparationStage
{
    Idle,
    Checking,
    SyncingPack,
    Downloading,
    InstallingJava,
    InstallingLoader,
    Verifying,
    Ready,
    Failed
}

/// <summary>
/// One report from the preparation, as the play button will say it.
/// </summary>
public sealed record RuntimePreparationProgress(
    RuntimePreparationStage Stage,
    string Message,
    double? Fraction = null,
    long DownloadedBytes = 0,
    long TotalBytes = 0);

public sealed record PreparedRuntime(
    string RuntimeRoot,
    string ProfileId,
    string JavaPath,
    string ClientJarPath,
    PackRuntimeDescriptor Descriptor)
{
    /// <summary>
    /// Where the libraries are, which is not under <see cref="RuntimeRoot"/>:
    /// they are shared by every build. Anything reading a jar out of the
    /// prepared game - the mappings the identity hooks are built from, authlib
    /// itself - has to look here.
    /// </summary>
    /// <remarks>
    /// It is a field rather than a path built from the runtime root because
    /// building it from the root is exactly what broke when the game was moved
    /// into one shared store: the mappings were fetched, the adapter looked for
    /// them where they used to be, found none, and every player lost their skin
    /// and the patched multiplayer screen at once.
    /// </remarks>
    public string LibrariesRoot { get; init; } = System.IO.Path.Combine(RuntimeRoot, "libraries");
}

public interface IPackLoaderProvider
{
    PackLoaderKind Kind { get; }
    bool RequiresInstallerJava => true;

    Task<string> InstallAsync(PackLoaderInstallationContext context, CancellationToken token);
}

public sealed record PackLoaderInstallationContext(
    PackRuntimeDescriptor Descriptor,
    string RuntimeRoot,
    /// <summary>
    /// Where the game itself is kept, which is shared by every build. A loader
    /// installer writes its profile and its libraries into a Minecraft folder,
    /// and that folder is this one rather than the build's - two builds on one
    /// loader version want the same profile, and the second must find it made.
    /// </summary>
    string GameRoot,
    string TemporaryRoot,
    string BaseVersionId,
    string JavaPath,
    HttpClient HttpClient,
    CmlLib.Core.MinecraftLauncher Launcher,
    IProgress<RuntimePreparationProgress>? Progress,
    Logger Logger)
{
    internal string? GameJavaPath { get; init; }
}
