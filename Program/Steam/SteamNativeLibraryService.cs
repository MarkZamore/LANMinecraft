using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Minecraft;

/// <summary>
/// Resolves the SDK 1.60 native prepared and leased by the native bootstrap.
/// Exact pins keep it paired with Steamworks.NET, independent of working directory.
/// </summary>
public sealed class SteamNativeLibraryService
{
    internal const string NativeFileName = "steam_api64.dll";

    /// <summary>SHA-256 of Windows-x64/steam_api64.dll from Steamworks.NET-Standalone_2024.8.0.zip.</summary>
    public const string NativeSha256 =
        "1add7f151fa644870a735ae86e68d1f019f296130d8e7c0a7ed3ecc7482dccbc";
    public const long NativeSizeBytes = 300_392;

    // Steamworks.NET declares [DllImport("steam_api64")] on 64-bit Windows;
    // older builds of the same package use the platform-neutral name.
    private static readonly string[] ResolvedLibraryNames =
    [
        "steam_api64",
        "steam_api"
    ];

    private readonly Logger? _logger;
    private readonly object _gate = new();
    private string? _nativePath;
    private bool _resolverRegistered;

    public SteamNativeLibraryService(AppPaths paths, Logger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates the prepared native and installs the import resolver.
    /// Safe to call repeatedly; the work happens once per process.
    /// </summary>
    public string Prepare()
    {
        lock (_gate)
        {
            _nativePath ??= Extract();
            if (!_resolverRegistered)
            {
                RegisterResolver(_nativePath);
                _resolverRegistered = true;
            }
            return _nativePath;
        }
    }

    internal string Extract()
    {
        var path = PreparedNativePath();
        if (new FileInfo(path).Length != NativeSizeBytes || HashFile(path) != NativeSha256)
            throw new InvalidDataException("The prepared Steamworks native does not match the managed SDK.");
        return path;
    }

    internal static byte[] ReadPreparedNative() => File.ReadAllBytes(PreparedNativePath());

    private static string PreparedNativePath() => BootstrapLaunchContext.Current.Resolve("steam-native", NativeFileName);

    private void RegisterResolver(string nativePath)
    {
        var steamworks = ResolveSteamworksAssembly();
        if (steamworks is null)
        {
            _logger?.Warn("Steamworks.NET assembly was not found; native resolver not installed.");
            return;
        }

        NativeLibrary.SetDllImportResolver(steamworks, (name, _, _) =>
            ResolvedLibraryNames.Contains(name, StringComparer.OrdinalIgnoreCase) &&
            NativeLibrary.TryLoad(nativePath, out var handle)
                ? handle
                : IntPtr.Zero);
    }

    private static Assembly? ResolveSteamworksAssembly()
    {
        try
        {
            return typeof(Steamworks.SteamAPI).Assembly;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
