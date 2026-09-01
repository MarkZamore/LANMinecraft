using System.Net.Http;

namespace Minecraft;

public enum PackLoaderKind
{
    Vanilla,
    Forge,
    NeoForge,
    Fabric,
    Quilt
}

public sealed record PackLoaderDescriptor(PackLoaderKind Type, string? Version);

public sealed record PackRuntimeDescriptor(
    int SchemaVersion,
    string MinecraftVersion,
    PackLoaderDescriptor Loader,
    string ClientJar,
    string DescriptorHash);

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
/// <remarks>
/// There used to be a pair of numbers here counting passes - "1/2", "2/2" -
/// which the button drew after the word "Файлы", where it read as a count of
/// files and was neither. It was a count of passes, and the passes were more
/// than two: the base game, then the loader's installer, then the loader's own
/// libraries, and the number sat at 2/2 while the bar went back to nothing
/// twice more. <see cref="Message"/> names what is being fetched instead, so a
/// bar that starts again says why by changing from "Minecraft" to "NeoForge".
/// </remarks>
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
    Logger Logger);
