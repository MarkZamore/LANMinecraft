using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Minecraft;

/// <summary>
/// Makes the Steamworks native library loadable from the single-file launcher.
///
/// Steamworks.NET ships managed assemblies only, so the matching SDK 1.60
/// steam_api64.dll is vendored in the repository, embedded into the executable
/// and extracted next to the other launcher runtimes on first use. The copy is
/// hash-verified before and after writing, exactly like the identity adapter
/// jar, and a DllImport resolver points Steamworks.NET at it so the load never
/// depends on the working directory or the single-file extraction folder.
/// </summary>
public sealed class SteamNativeLibraryService
{
    internal const string ResourceName = "Minecraft.SteamApi64.dll";
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

    private readonly AppPaths _paths;
    private readonly Logger? _logger;
    private readonly object _gate = new();
    private string? _nativePath;
    private bool _resolverRegistered;

    public SteamNativeLibraryService(AppPaths paths, Logger? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Extracts the native (if needed) and installs the import resolver.
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
        var bytes = ReadEmbeddedNative();
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualHash, NativeSha256, StringComparison.Ordinal) ||
            bytes.LongLength != NativeSizeBytes)
        {
            throw new InvalidDataException(
                "The embedded Steamworks native does not match the pinned SDK build.");
        }

        var directory = Path.Combine(_paths.SteamNative, NativeSha256[..12]);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, NativeFileName);
        if (File.Exists(destination) &&
            string.Equals(HashFile(destination), NativeSha256, StringComparison.Ordinal))
        {
            return destination;
        }

        var temporary = destination + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (!string.Equals(HashFile(temporary), NativeSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The extracted Steamworks native failed SHA-256 validation.");
            }
            File.Move(temporary, destination, overwrite: true);
        }
        catch (IOException) when (File.Exists(destination) &&
            string.Equals(HashFile(destination), NativeSha256, StringComparison.Ordinal))
        {
            // A second launcher instance published the same bytes first.
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        _logger?.Info($"Steamworks native prepared at {destination}.");
        return destination;
    }

    internal static byte[] ReadEmbeddedNative()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded Steamworks native is missing.");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        return memory.ToArray();
    }

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
