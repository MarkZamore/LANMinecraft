using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minecraft;

internal sealed record PackArchiveInfo(string PackName, long SizeBytes);

/// <summary>A transferable snapshot of the canonical pack folder. Installation never writes into an existing pack or instance.</summary>
internal sealed class PackArchiveService(AppPaths paths, Func<string, bool> canExport,
    Func<string, bool> reservedName, Func<string, bool>? isBusy = null,
    Func<string, CancellationToken, Task>? prepareDescriptor = null)
{
    internal const string Extension = ".lanpack";
    internal const long MaxArchiveBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxPayloadBytes = 16L * 1024 * 1024 * 1024;
    private const int MaxEntries = 100_000;
    private const int MaxManifestBytes = 32 * 1024 * 1024;
    private const long MaxDirectoryBytes = 64L * 1024 * 1024;
    private const int BufferSize = 128 * 1024;
    private const string MetadataName = ".pack-transfer.json";
    private const string PayloadPrefix = "pack/";

    private static readonly HashSet<string> PersonalPackRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "mods", "config", "defaultconfigs", "kubejs", "scripts", "resourcepacks", "shaderpacks", "texturepacks",
        "data", "datapacks", "patchouli_books", "global_packs", "globalpacks", "resources", "coremods", "launcher", "native"
    };

    private static readonly HashSet<string> PrivateSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "worlds", "playerdata", "logs", "screenshots", "crash-reports",
        "backups", "local", "xaero", "search_index", "launcher-runtime", ".git", "usercache.json", "usernamecache.json",
        "servers.dat", "servers.dat_old", "level.dat", "level.dat_old", "command_history.txt", "steam-identity.json",
        "UUID.json", "identity-profiles.properties", "skin-profiles.properties"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true
    };

    internal Task ExportAsync(string packRelativePath, string archivePath,
        IProgress<double>? progress, CancellationToken token) =>
        Task.Run(() => ExportCoreAsync(packRelativePath, archivePath, progress, token), token);

    internal Task<PackArchiveInfo> InspectAsync(string archivePath, CancellationToken token) => Task.Run(async () =>
    {
        using var input = OpenArchive(archivePath);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = await ReadManifestAsync(archive, paths.Packs, token).ConfigureAwait(false);
        return new PackArchiveInfo(manifest.PackName, input.Length);
    }, token);

    internal Task<string> ImportAsync(string archivePath, IProgress<double>? progress,
        CancellationToken token, string? expectedPackName = null) =>
        Task.Run(() => ImportCoreAsync(archivePath, progress, token, expectedPackName), token);

    internal void CheckCanImport(string packName)
    {
        ValidatePackName(packName, paths.Packs);
        EnsureOrdinaryDirectory(paths.Packs);
    }

    private async Task ExportCoreAsync(string packName, string archivePath,
        IProgress<double>? progress, CancellationToken token)
    {
        ValidatePackName(packName, paths.Packs);
        if (!canExport(packName) || isBusy?.Invoke(packName) == true)
            throw new IOException("Для передачи выберите личную сборку и закройте игру.");
        var source = paths.CombineUnderPacks(packName);
        EnsureOrdinaryDirectory(source);
        if (PortablePackSyncService.IsUpdatePending(source))
            throw new InvalidDataException("Обновление сборки не закончено. Сначала подготовьте сборку через «Играть».");
        if (!PackManifestService.HasManifest(source) && prepareDescriptor is not null)
            await prepareDescriptor(packName, token).ConfigureAwait(false);
        if (!PackManifestService.HasManifest(source))
            throw new InvalidDataException("Состав сборки не определён или её обновление не закончено. Сначала подготовьте сборку через «Играть».");
        PortablePackSyncService.ValidateManifestPath(PackManifestService.ManifestFileName, source);
        var descriptor = PackManifestService.Load(source);
        var managed = Exists(Path.Combine(source, PortablePackSyncService.SyncStateFileName));
        var inventory = managed
            ? PortablePackSyncService.ReadInstalledSnapshot(paths, source)
            : EnumeratePersonal(source, descriptor, token);
        var features = managed ? PortablePackSyncService.ReadInstalledSnapshotFeatures(source) : [];
        var total = ValidateInventory(inventory, source, managed, descriptor.ClientJar, requireHash: managed);
        ValidateRuntimePayload(descriptor, inventory);
        var finalPath = Path.GetFullPath(archivePath);
        var temporary = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var ownsTemporary = false;
        try
        {
            using (var output = OpenNew(temporary))
            {
                ownsTemporary = true;
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var buffer = new byte[BufferSize];
                var written = new List<PortablePackSyncService.ManagedFileSnapshot>(inventory.Count);
                var transferred = 0L;
                var measuredAt = 0L;
                foreach (var file in inventory.OrderBy(file => file.Path, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    var from = PortablePackSyncService.ValidateManifestPath(file.Path, source);
                    using var input = OpenRead(Under(source, from));
                    if (input.Length != file.SizeBytes) throw ChangedPack();
                    var entry = archive.CreateEntry(PayloadPrefix + file.Path, CompressionLevel.Fastest);
                    using var item = entry.Open();
                    var sha256 = await CopyVerifiedAsync(input, item, file.SizeBytes, buffer, Progress, token).ConfigureAwait(false);
                    if (managed && !sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw ChangedPack();
                    written.Add(file with { Sha256 = sha256 });
                    if (output.Length > MaxArchiveBytes) throw TooLarge();
                }

                var metadata = new PackArchiveManifest(PortableFormat.SchemaVersion, packName, managed, features,
                    descriptor.ClientJar, written);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
                if (bytes.Length > MaxManifestBytes) throw TooLarge();
                using var manifest = archive.CreateEntry(MetadataName, CompressionLevel.Fastest).Open();
                await manifest.WriteAsync(bytes, token).ConfigureAwait(false);

                void Progress(int bytesWritten)
                {
                    transferred += bytesWritten;
                    if (transferred - measuredAt >= 4L * 1024 * 1024)
                    {
                        measuredAt = transferred;
                        if (output.Length > MaxArchiveBytes) throw TooLarge();
                    }
                    Report(progress, transferred, total);
                }
            }

            token.ThrowIfCancellationRequested();
            using (OpenArchive(temporary)) { }
            if (!canExport(packName) || isBusy?.Invoke(packName) == true || PortablePackSyncService.IsUpdatePending(source))
                throw ChangedPack();
            File.Move(temporary, finalPath);
            ownsTemporary = false;
            progress?.Report(1);
        }
        finally
        {
            if (ownsTemporary)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<string> ImportCoreAsync(string archivePath, IProgress<double>? progress,
        CancellationToken token, string? expectedPackName)
    {
        using var input = OpenArchive(archivePath);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = await ReadManifestAsync(archive, paths.Packs, token).ConfigureAwait(false);
        if (expectedPackName is not null && !manifest.PackName.Equals(expectedPackName, StringComparison.Ordinal))
            throw new InvalidDataException("Имя сборки в архиве не совпадает с предложением отправителя.");
        CheckCanImport(manifest.PackName);
        var total = manifest.Files.Sum(file => file.SizeBytes);
        if (new DriveInfo(Path.GetPathRoot(paths.Packs)!).AvailableFreeSpace < total)
            throw new IOException("Недостаточно места для распаковки сборки.");
        EnsureOrdinaryDirectory(paths.Launcher);
        using var lease = PortablePackSyncService.CreateTransferStage(paths, out var stage);
        var staging = Path.Combine(stage, "payload");
        Directory.CreateDirectory(staging);
        var buffer = new byte[BufferSize];
        var transferred = 0L;
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var destination = Under(staging, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var payload = archive.GetEntry(PayloadPrefix + file.Path)!.Open();
            using var output = OpenNew(destination);
            var sha256 = await CopyVerifiedAsync(payload, output, file.SizeBytes, buffer, Progress, token).ConfigureAwait(false);
            if (!sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма файла сборки не совпала: " + file.Path);
        }
        var descriptor = PackManifestService.Load(staging);
        if (!descriptor.ClientJar.Equals(manifest.ClientJar, StringComparison.Ordinal))
            throw new InvalidDataException("Описание сборки и список файлов указывают разные клиентские JAR.");
        ValidateRuntimePayload(descriptor, manifest.Files);
        if (manifest.Managed)
            PortablePackSyncService.WriteImportedSnapshot(staging, manifest.Files, manifest.RequiredFeatures);

        using var installGate = await SharedRuntimeGate.AcquireAsync(paths, token).ConfigureAwait(false);
        var installedName = await InstallWithoutOverwriteAsync(staging, manifest, token).ConfigureAwait(false);
        progress?.Report(1);
        return installedName;

        void Progress(int bytesRead)
        {
            transferred += bytesRead;
            Report(progress, transferred, total);
        }
    }

    private async Task<string> InstallWithoutOverwriteAsync(string staging, PackArchiveManifest manifest, CancellationToken token)
    {
        EnsureOrdinaryDirectory(paths.Packs);
        EnsureOrdinaryDirectory(paths.Worlds);
        EnsureOrdinaryDirectory(paths.Instances);
        var flatWorldBuilds = ReadFlatWorldBuilds(token);
        for (var index = 1; index <= 1000; index++)
        {
            token.ThrowIfCancellationRequested();
            var name = NumberedName(manifest.PackName, index);
            var destination = paths.CombineUnderPacks(name);
            if (await MatchesInstalledCopyAsync(name, destination, manifest, token).ConfigureAwait(false)) return name;
            if (reservedName(name) || Exists(destination) || Exists(paths.CombineUnderInstances(name)) ||
                flatWorldBuilds.Contains(WorldLocations.CanonicalBuildPath(name)) ||
                Exists(WorldLocations.ForBuild(paths.Worlds, name)) || isBusy?.Invoke(name) == true) continue;
            try
            {
                Directory.Move(staging, destination);
                return name;
            }
            catch (IOException) when (Exists(destination))
            {
                // A second launcher may have installed this name since the check.
                if (await MatchesInstalledCopyAsync(name, destination, manifest, token).ConfigureAwait(false)) return name;
            }
        }
        throw new IOException("Не удалось подобрать свободное имя для полученной сборки.");
    }

    private HashSet<string> ReadFlatWorldBuilds(CancellationToken token)
    {
        var builds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadata = new WorldMetadataService();
        foreach (var directory in Directory.EnumerateDirectories(paths.Worlds))
        {
            token.ThrowIfCancellationRequested();
            var file = Path.Combine(directory, WorldMetadataService.MetadataFileName);
            if (!MaintenancePath.TryRead(paths, file, out var attributes) || attributes.HasFlag(FileAttributes.Directory) ||
                new FileInfo(file).Length > 1024 * 1024 || !WorldLocations.IsWorld(directory)) continue;
            var build = WorldLocations.CanonicalBuildPath(metadata.Read(directory)?.BuildRelativePath);
            if (build.Length != 0) builds.Add(build);
        }
        return builds;
    }

    private async Task<bool> MatchesInstalledCopyAsync(string name, string directory, PackArchiveManifest manifest, CancellationToken token)
    {
        if (isBusy?.Invoke(name) == true || !Directory.Exists(directory) || !canExport(name) ||
            !MaintenancePath.TryRead(paths, directory, out var attributes) || !attributes.HasFlag(FileAttributes.Directory)) return false;
        try
        {
            if (!PackManifestService.HasManifest(directory) || PortablePackSyncService.IsUpdatePending(directory)) return false;
            PortablePackSyncService.ValidateManifestPath(PackManifestService.ManifestFileName, directory);
            var managed = Exists(Path.Combine(directory, PortablePackSyncService.SyncStateFileName))
                ? PortablePackSyncService.ReadInstalledSnapshot(paths, directory)
                : null;
            if ((managed is not null) != manifest.Managed) return false;
            var descriptor = PackManifestService.Load(directory);
            var files = managed ?? EnumeratePersonal(directory, descriptor, token);
            if (files.Count != manifest.Files.Count || !descriptor.ClientJar.Equals(manifest.ClientJar, StringComparison.Ordinal) ||
                manifest.Managed && !manifest.RequiredFeatures.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(PortablePackSyncService.ReadInstalledSnapshotFeatures(directory))) return false;
            var actual = files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var expected in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                if (!actual.TryGetValue(expected.Path, out var file) || file.SizeBytes != expected.SizeBytes ||
                    manifest.Managed && !string.Equals(file.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
                var relative = PortablePackSyncService.ValidateManifestPath(file.Path, directory);
                using var input = OpenRead(Under(directory, relative));
                if (input.Length != expected.SizeBytes) return false;
                var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
                if (!sha.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return !PortablePackSyncService.IsUpdatePending(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static string NumberedName(string name, int index)
    {
        if (index == 1) return name;
        var suffix = $" ({index.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
        var limit = 120 - suffix.Length;
        if (name.Length > limit)
        {
            if (char.IsHighSurrogate(name[limit - 1])) limit--;
            name = name[..limit].TrimEnd('.', ' ');
        }
        return name + suffix;
    }

    private static async Task<PackArchiveManifest> ReadManifestAsync(ZipArchive archive, string validationRoot,
        CancellationToken token)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var unixKind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (entries.Count >= MaxEntries + 1 || !entries.TryAdd(entry.FullName, entry) ||
                unixKind is not (0 or 0x8000) ||
                (entry.ExternalAttributes & ((int)FileAttributes.ReparsePoint | (int)FileAttributes.Directory)) != 0)
                throw new InvalidDataException("Архив содержит повторяющееся имя, каталог или ссылку.");
        }
        if (!entries.TryGetValue(MetadataName, out var metadata) || metadata.FullName != MetadataName ||
            metadata.Length is <= 0 or > MaxManifestBytes)
            throw new InvalidDataException("В архиве нет допустимого описания сборки.");
        var bytes = new byte[checked((int)metadata.Length)];
        using var input = metadata.Open();
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (input.ReadByte() != -1) throw new InvalidDataException("Размер описания сборки не совпал.");
        PackArchiveManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<PackArchiveManifest>(bytes, JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException("Описание архива сборки повреждено.", ex); }
        if (manifest is null || !PortableFormat.CanRead(manifest.SchemaVersion) ||
            manifest.PackName is null || manifest.ClientJar is null || manifest.Files is null ||
            manifest.RequiredFeatures is null || manifest.RequiredFeatures.Length > 64 ||
            manifest.ClientJar.Length > 255 || manifest.ClientJar.Contains('/') || manifest.ClientJar.Contains('\\') ||
            !manifest.Managed && manifest.RequiredFeatures.Length != 0)
            throw new InvalidDataException("Формат архива сборки не поддерживается.");
        ValidatePackName(manifest.PackName, validationRoot);
        _ = ValidateInventory(manifest.Files, validationRoot, manifest.Managed, manifest.ClientJar, requireHash: true);
        if (entries.Count != manifest.Files.Count + 1)
            throw new InvalidDataException("Список файлов не совпадает с содержимым архива сборки.");
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var name = PayloadPrefix + file.Path;
            if (!entries.TryGetValue(name, out var entry) || entry.FullName != name || entry.Length != file.SizeBytes)
                throw new InvalidDataException("В архиве нет файла нужного размера: " + file.Path);
        }
        return manifest;
    }

    private static long ValidateInventory(IReadOnlyList<PortablePackSyncService.ManagedFileSnapshot> files,
        string root, bool managed, string clientJar, bool requireHash)
    {
        if (files.Count is 0 or > MaxEntries) throw TooLarge();
        var pathsInArchive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0L;
        foreach (var file in files)
        {
            if (file is null || file.Path is null || file.SizeBytes < 0 || file.SizeBytes > MaxPayloadBytes - total ||
                requireHash && (file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit)))
                throw new InvalidDataException("Некорректный размер или контрольная сумма файла сборки.");
            ValidatePayloadPath(file.Path, root);
            if (!IsShareable(file.Path, managed, clientJar) || !pathsInArchive.Add(file.Path))
                throw new InvalidDataException("Архив содержит личный, служебный или повторяющийся путь: " + file.Path);
            total += file.SizeBytes;
        }
        foreach (var path in pathsInArchive)
        {
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
                if (pathsInArchive.Contains(path[..slash]))
                    throw new InvalidDataException("Имя файла совпадает с именем папки: " + path);
        }
        if (!pathsInArchive.Contains(PackManifestService.ManifestFileName))
            throw new InvalidDataException("В архиве нет описания запуска portable-pack.json.");
        return total;
    }

    private static IReadOnlyList<PortablePackSyncService.ManagedFileSnapshot> EnumeratePersonal(string source,
        PackRuntimeDescriptor descriptor, CancellationToken token)
    {
        var files = new List<PortablePackSyncService.ManagedFileSnapshot>();
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, entry).Replace('\\', '/');
                var attributes = File.GetAttributes(entry);
                if (relative.IndexOf('/') < 0 && attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!PersonalPackRoots.Contains(relative)) continue;
                }
                else if (!IsShareable(relative, managed: false, descriptor.ClientJar)) continue;

                ValidatePayloadPath(relative, source);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Сборка содержит ссылку на файл или папку: " + relative);
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(entry);
                else
                {
                    if (files.Count >= MaxEntries) throw TooLarge();
                    files.Add(new(relative, new FileInfo(entry).Length, ""));
                }
            }
        }
        return files;
    }

    private static bool IsShareable(string relative, bool managed, string clientJar)
    {
        var segments = relative.Split('/');
        var root = segments[0];
        if (IsPrivatePayloadPath(relative)) return false;
        if (segments.Length == 1)
        {
            // These are pack-side defaults, never settings from Personal/Instances.
            if (root.Equals("options.txt", StringComparison.OrdinalIgnoreCase) ||
                root.Equals(PackManifestService.ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                root.Equals(clientJar, StringComparison.OrdinalIgnoreCase)) return true;
            return managed && !PackInstanceService.InstanceOwnedFiles.Contains(root);
        }
        if (PackInstanceService.InstanceOwnedDirectories.Contains(root)) return false;
        return managed || PersonalPackRoots.Contains(root);
    }

    internal static bool IsPrivatePayloadPath(string relative) =>
        relative.Split('/').Any(segment => PrivateSegments.Contains(segment) ||
            segment.StartsWith(".portable-", StringComparison.OrdinalIgnoreCase) ||
            segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) ||
        relative.Equals("config/jei/world/server", StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith("config/jei/world/server/", StringComparison.OrdinalIgnoreCase) ||
        PortablePackSyncService.IsServiceRelativePath(relative.Split('/')[0]);

    internal static void ValidatePackName(string name, string root)
    {
        if (!PackCatalogService.ValidFolder(name) || name.StartsWith('.'))
            throw new InvalidDataException("Недопустимое имя сборки.");
        ValidatePayloadPath(name, root);
    }

    private static void ValidatePayloadPath(string path, string root)
    {
        PortablePackSyncService.ValidateManifestPath(path, root, checkAncestors: false);
        if (path.Split('/').Any(segment =>
        {
            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return true;
            return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[^1]);
        })) throw new InvalidDataException("Недопустимый путь Windows в архиве сборки: " + path);
    }

    private static void ValidateRuntimePayload(PackRuntimeDescriptor descriptor,
        IReadOnlyList<PortablePackSyncService.ManagedFileSnapshot> inventory)
    {
        var expected = descriptor.NativeRuntime?.Executable ?? descriptor.ClientJar;
        if (expected.Length != 0 && !inventory.Any(file => file.Path.Equals(expected, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("В архиве нет файла, указанного для запуска сборки: " + expected);
    }

    private void EnsureOrdinaryDirectory(string directory)
    {
        if (!MaintenancePath.TryRead(paths, directory, out var attributes) || !attributes.HasFlag(FileAttributes.Directory))
            throw new IOException("Папка сборок недоступна или является ссылкой.");
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static string Under(string directory, string relative) =>
        Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream OpenNew(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream OpenArchive(string path)
    {
        var input = OpenRead(path);
        try
        {
            CheckCentralDirectory(input);
            input.Position = 0;
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    // Bound metadata before ZipArchive materializes attacker-controlled central directory entries.
    // Our format emits no ZIP comment and only allows single-disk ZIP/ZIP64.
    private static void CheckCentralDirectory(FileStream stream)
    {
        if (stream.Length is < 22 or > MaxArchiveBytes) throw TooLarge();
        Span<byte> end = stackalloc byte[22];
        var endOffset = stream.Length - end.Length;
        stream.Position = endOffset;
        stream.ReadExactly(end);
        var diskCount = BinaryPrimitives.ReadUInt16LittleEndian(end[8..]);
        long count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        long size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        long start = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06054b50 ||
            BinaryPrimitives.ReadUInt32LittleEndian(end[4..]) != 0 || diskCount != count ||
            BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != 0) throw BadZip();

        if (count == ushort.MaxValue || size == uint.MaxValue || start == uint.MaxValue)
        {
            Span<byte> locator = stackalloc byte[20];
            var locatorOffset = endOffset - locator.Length;
            if (locatorOffset < 56) throw BadZip();
            stream.Position = locatorOffset;
            stream.ReadExactly(locator);
            endOffset = BinaryPrimitives.ReadInt64LittleEndian(locator[8..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50 ||
                BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) != 1 ||
                endOffset < 0 || endOffset != locatorOffset - 56) throw BadZip();
            Span<byte> zip64 = stackalloc byte[56];
            stream.Position = endOffset;
            stream.ReadExactly(zip64);
            count = BinaryPrimitives.ReadInt64LittleEndian(zip64[32..]);
            size = BinaryPrimitives.ReadInt64LittleEndian(zip64[40..]);
            start = BinaryPrimitives.ReadInt64LittleEndian(zip64[48..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064b50 ||
                BinaryPrimitives.ReadUInt64LittleEndian(zip64[4..]) != 44 ||
                BinaryPrimitives.ReadUInt64LittleEndian(zip64[16..]) != 0 ||
                BinaryPrimitives.ReadInt64LittleEndian(zip64[24..]) != count) throw BadZip();
        }

        if (count is < 2 or > MaxEntries + 1 || size < count * 46 || size > MaxDirectoryBytes ||
            start < 0 || start != endOffset - size) throw BadZip();
    }

    private static async Task<string> CopyVerifiedAsync(Stream input, Stream output, long size, byte[] buffer,
        Action<int> progress, CancellationToken token)
    {
        var remaining = size;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (remaining != 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException("Файл сборки закончился раньше указанного размера.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            remaining -= count;
            progress(count);
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Файл сборки превышает указанный размер.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Report(IProgress<double>? progress, long current, long total)
    {
        if (total != 0) progress?.Report(Math.Min(0.99, (double)current / total));
    }

    private static InvalidDataException ChangedPack() => new("Состав сборки изменился во время передачи. Попробуйте ещё раз.");
    private static InvalidDataException TooLarge() => new("Архив сборки превышает допустимый размер или число файлов.");
    private static InvalidDataException BadZip() => new("Оглавление архива сборки повреждено.");

    private sealed record PackArchiveManifest(int SchemaVersion, string PackName, bool Managed, string[] RequiredFeatures,
        string ClientJar, IReadOnlyList<PortablePackSyncService.ManagedFileSnapshot> Files);
}
