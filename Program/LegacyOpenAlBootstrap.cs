using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using CmlLib.Core;
using CmlLib.Core.Version;

namespace Minecraft;

/// <summary>Load the original LWJGL 2 audio DLL through Java's Unicode Windows loader.</summary>
internal static class LegacyOpenAlBootstrap
{
    internal static IReadOnlyList<string> Prepare(AppPaths paths, MinecraftLauncher launcher,
        IVersion profile, string? nativesDirectory)
    {
        var libraries = profile.ConcatInheritedCollection(version => version.Libraries).ToArray();
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(nativesDirectory) ||
            !libraries.Any(library => library.Name?.StartsWith("org.lwjgl.lwjgl:lwjgl:2.", StringComparison.Ordinal) == true))
            return [];

        // BuildProcess has already selected and extracted this exact directory.
        // No recursive search, alternate DLL, system installation or copied native.
        var name = Environment.Is64BitProcess ? "OpenAL-amd64.dll" : "OpenAL32.dll";
        var dll = Path.GetFullPath(Path.Combine(nativesDirectory, name));
        EnsureOrdinaryPath(paths, dll);
        if (!File.Exists(dll)) return [];

        byte[]? expected = null;
        foreach (var library in libraries)
        {
            if (!library.IsClientRequired || library.Rules is not null &&
                !launcher.RulesEvaluator.Match(library.Rules, launcher.RulesContext)) continue;
            var native = library.GetNativeLibraryPath(launcher.RulesContext.OS);
            if (string.IsNullOrEmpty(native)) continue;
            var archivePath = Path.GetFullPath(Path.Combine(launcher.MinecraftPath.Library, native));
            EnsureOrdinaryPath(paths, archivePath);
            using var archive = ZipFile.OpenRead(archivePath);
            // CmlLib extracts eligible libraries in this order; the last entry
            // wins. Its exclude list is a ZIP-entry prefix list.
            foreach (var entry in archive.Entries)
            {
                if (!string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase) ||
                    library.ExtractExcludes?.Any(exclude => entry.FullName.StartsWith(exclude, StringComparison.Ordinal)) == true) continue;
                using var input = entry.Open();
                expected = SHA256.HashData(input);
            }
        }
        if (expected is null) return [];
        using (var installed = File.OpenRead(dll))
            if (!CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(installed)))
                throw new InvalidDataException("The extracted OpenAL DLL does not match its verified runtime archive.");

        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Minecraft.NativeInteropAgent.jar")
            ?? throw new InvalidOperationException("The launcher is missing its native interoperability bootstrap.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var agent = Path.Combine(paths.Launcher, "NativeInterop", hash, "launcher-native-interop-agent.jar");
        EnsureOrdinaryPath(paths, agent);
        if (!File.Exists(agent) || !SHA256.HashData(File.ReadAllBytes(agent)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
            AtomicFile.WriteAllBytes(agent, bytes);
        return [$"-javaagent:{agent}", $"-Dlanminecraft.native.openal={dll}",
            $"-Dlanminecraft.native.openal.sha256={Convert.ToHexString(expected).ToLowerInvariant()}"];
    }

    private static void EnsureOrdinaryPath(AppPaths paths, string path)
    {
        paths.EnsureUnderRoot(path);
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Native interoperability paths cannot contain a reparse point.");
    }
}
