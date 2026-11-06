using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Minecraft;

/// <summary>Holds the exact synced bytes used by a native process for its lifetime.</summary>
internal sealed class NativeRuntimeFiles : IDisposable
{
    private readonly List<FileStream> _opened = [];
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    private NativeRuntimeFiles(string executablePath) => ExecutablePath = executablePath;

    internal string ExecutablePath { get; }

    internal static NativeRuntimeFiles Open(string packDirectory, PackRuntimeDescriptor descriptor, CancellationToken token)
    {
        var runtime = descriptor.NativeRuntime ?? throw new ArgumentException("A native runtime is required.", nameof(descriptor));
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture is not Architecture.X64)
            throw new PlatformNotSupportedException("Для этой сборки требуется Windows x64.");
        if (PortablePackSyncService.IsUpdatePending(packDirectory))
            throw new InvalidDataException("Обновление сборки прервано. Повторите «Играть» с подключением к сети.");

        var snapshot = PortablePackSyncService.ReadManagedFiles(packDirectory,
            [PackManifestService.NativeRuntimeFeature, PackManifestService.NativeSteamFeature,
                PackManifestService.Minecraft26WorldLayoutFeature])
            ?? throw new InvalidDataException("Нативная сборка должна быть установлена из проверенного манифеста.");
        var root = Path.GetFullPath(packDirectory);
        var prefix = PackManifestService.NativePayloadRoot + "/";
        var nativeFiles = snapshot.Where(file => file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        var executable = nativeFiles.SingleOrDefault(file => file.Path.Equals(runtime.Executable, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The native executable is missing from the verified manifest.");
        var steamLibrary = executable.Path[..(executable.Path.LastIndexOf('/') + 1)] +
            SteamNativeLibraryService.NativeFileName;
        if (!nativeFiles.Any(file => file.Path.Equals(steamLibrary, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The native Steam library is missing from the verified manifest.");
        var manifest = snapshot.SingleOrDefault(file =>
            file.Path.Equals(PackManifestService.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The native descriptor is missing from the verified manifest.");

        var result = new NativeRuntimeFiles(Path.Combine(root, executable.Path.Replace('/', Path.DirectorySeparatorChar)));
        try
        {
            result.OpenVerified(root, manifest);
            if (PackManifestService.Load(root).DescriptorHash != descriptor.DescriptorHash)
                throw new InvalidDataException("The native descriptor changed during preparation. Retry Play.");
            foreach (var file in nativeFiles)
            {
                token.ThrowIfCancellationRequested();
                result.OpenVerified(root, file);
            }
            result.CheckContents(root, Path.Combine(root, PackManifestService.NativePayloadRoot), token);
            if (PortablePackSyncService.IsUpdatePending(root))
                throw new InvalidDataException("The native payload changed during preparation. Retry Play.");
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private void OpenVerified(string root, PortablePackSyncService.ManagedFileSnapshot file)
    {
        var path = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _opened.Add(stream);
        if (stream.Length != file.SizeBytes ||
            !Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Файл сборки не прошёл проверку SHA-256: {file.Path}. Повторите «Играть» с подключением к сети.");
        var executable = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);
        var steamLibrary = Path.GetFileName(path).Equals(SteamNativeLibraryService.NativeFileName, StringComparison.OrdinalIgnoreCase);
        if (executable || steamLibrary)
        {
            stream.Position = 0;
            using var image = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var headers = image.PEHeaders;
            if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus ||
                ((headers.CoffHeader.Characteristics & Characteristics.Dll) != 0) != steamLibrary)
                throw new InvalidDataException($"Native binary is not the required Windows x64 image: {file.Path}");
        }
        _files.Add(file.Path);
    }

    private void CheckContents(string root, string directory, CancellationToken token)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Native payload cannot contain filesystem links.");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Native payload cannot contain filesystem links.");
            if ((attributes & FileAttributes.Directory) != 0)
                CheckContents(root, path, token);
            else if (!_files.Contains(Path.GetRelativePath(root, path).Replace('\\', '/')))
                throw new InvalidDataException($"Unexpected file in the native payload: {Path.GetFileName(path)}");
        }
    }

    public void Dispose()
    {
        foreach (var file in _opened) file.Dispose();
        _opened.Clear();
    }
}
