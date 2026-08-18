using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

/// <summary>What one player sends another after something went wrong.</summary>
public sealed record BugReportManifest
{
    public const string ProtocolName = "MinecraftPortableBugReport";

    /// <summary>The launcher's one protocol version; see <see cref="PortableFormat"/>.</summary>
    public const int ProtocolVersion = PortableFormat.ProtocolVersion;

    public string Protocol { get; init; } = ProtocolName;
    public int Version { get; init; } = ProtocolVersion;

    /// <summary>Identifies the report on both sides; also its directory name.</summary>
    public string ReportId { get; init; } = "";

    public string SenderSteamId64 { get; init; } = "";
    public string SenderPersonaName { get; init; } = "";
    public string SenderPlayerName { get; init; } = "";

    /// <summary>What the player typed. Optional - a report with no words is still useful.</summary>
    public string Message { get; init; } = "";

    public DateTimeOffset CreatedAtUtc { get; init; }
    public string LauncherVersion { get; init; } = "";
    public string PackName { get; init; } = "";
    public string PackHash { get; init; } = "";

    /// <summary>Size and hash of the zip that follows the manifest on the wire.</summary>
    public long ArchiveBytes { get; init; }
    public string ArchiveSha256 { get; init; } = "";

    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>The receiving side's answer, in the shape the transfer protocol uses.</summary>
public sealed record BugReportAck
{
    public string Protocol { get; init; } = BugReportManifest.ProtocolName;
    public int Version { get; init; } = BugReportManifest.ProtocolVersion;
    public bool Ok { get; init; }
    public string Stage { get; init; } = "";
    public string ReportId { get; init; } = "";
    public string Message { get; init; } = "";
}

/// <summary>
/// Bug reports: a player who was thrown out of the game, hit a bug, or saw the
/// launcher misbehave presses one button, optionally types what happened, and
/// the last of their logs travels to a friend as a single archive.
///
/// This deliberately replaces the live log stream. A stream only helps someone
/// who is already watching at the moment of the failure; a report is written
/// when the player notices, arrives whole, and sits in a folder that can be
/// read - by a person or by a tool - long afterwards.
///
/// The wire shape follows the world transfer: a JSON header naming the
/// protocol, the bytes, then an acknowledgement.
/// </summary>
public sealed class BugReportService : IPortableProtocolHandler
{
    /// <summary>Enough of a session to explain a crash, small enough to send in seconds.</summary>
    internal const long MaxArchiveBytes = 64L * 1024 * 1024;
    internal const int MaxMessageCharacters = 4000;

    /// <summary>
    /// The two logs that actually explain a session - the launcher's own and
    /// the running game's. Ten mebibytes is deep enough to reach past a modded
    /// startup into what the player was doing; log text compresses roughly ten
    /// times, so the report still travels in seconds.
    /// </summary>
    private const int MaxLiveLogTailBytes = 10 * 1024 * 1024;

    /// <summary>Small enough that the progress line moves, large enough to be free.</summary>
    private const int SendChunkBytes = 256 * 1024;

    /// <summary>
    /// Everything else in the bundle is supporting evidence: a rotated archive
    /// or a crash report, where the interesting part is at the end and short.
    /// </summary>
    private const int MaxSupportingLogTailBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Reports older than this have been overtaken by whatever happened since.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    internal const long MaxStoredBytes = 256L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly IPeerTransport _transport;
    private readonly SupportLogSanitizer _sanitizer;
    private readonly Func<string?> _instanceDirectoryProvider;
    private readonly Func<BugReportContext> _contextProvider;
    private readonly Func<CancellationToken, Task<SupportEnvironmentSnapshot>>? _environmentProvider;
    private readonly TimeProvider _timeProvider;

    public BugReportService(
        AppPaths paths,
        Logger logger,
        IPeerTransport transport,
        Func<string?> instanceDirectoryProvider,
        Func<BugReportContext> contextProvider,
        Func<CancellationToken, Task<SupportEnvironmentSnapshot>>? environmentProvider = null,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _instanceDirectoryProvider = instanceDirectoryProvider ??
            throw new ArgumentNullException(nameof(instanceDirectoryProvider));
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        _environmentProvider = environmentProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sanitizer = SupportLogSanitizer.CreateDefault(paths);
    }

    /// <summary>Raised when a report arrives, so the window can say so.</summary>
    public event EventHandler<BugReportArrival>? ReportReceived;

    string IPortableProtocolHandler.ProtocolName => BugReportManifest.ProtocolName;

    /// <summary>Where received reports are kept, one directory per report.</summary>
    public string ReportsDirectory => _paths.BugReports;

    /// <summary>
    /// Packs the last of this player's logs and sends them to a friend. The
    /// archive is built first so a failure to read a log is reported before
    /// the friend is disturbed.
    /// </summary>
    public async Task<BugReportManifest> SendAsync(
        SteamId64 recipient,
        string message,
        IProgress<BugReportProgress>? progress = null,
        CancellationToken token = default)
    {
        if (!recipient.IsValid) throw new ArgumentException("The recipient is not a Steam account.", nameof(recipient));
        if (!_transport.IsAvailable) throw new InvalidOperationException(_transport.UnavailableReason);

        var context = _contextProvider();
        var reportId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(_paths.Personal, "Temp", "BugReports", reportId);
        Directory.CreateDirectory(staging);
        var archivePath = Path.Combine(staging, "report.zip");

        try
        {
            var environment = await CaptureEnvironmentAsync(token).ConfigureAwait(false);
            var files = BuildArchive(archivePath, context, message, environment);
            var info = new FileInfo(archivePath);
            var manifest = new BugReportManifest
            {
                ReportId = reportId,
                SenderSteamId64 = context.SteamId64.ToString(),
                SenderPersonaName = context.PersonaName,
                SenderPlayerName = context.PlayerName,
                Message = NormalizeMessage(message),
                CreatedAtUtc = _timeProvider.GetUtcNow(),
                LauncherVersion = context.LauncherVersion,
                PackName = context.PackName,
                PackHash = context.PackHash,
                ArchiveBytes = info.Length,
                ArchiveSha256 = HashFile(archivePath),
                Files = files
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(SendTimeout);
            await using var connection = await _transport
                .ConnectAsync(recipient, BugReportManifest.ProtocolName, timeout.Token)
                .ConfigureAwait(false);
            await PortableProtocol.WriteJsonAsync(connection.Stream, manifest, JsonOptions, timeout.Token)
                .ConfigureAwait(false);

            // The receiver decides on the manifest alone. Streaming first meant
            // a refusal - a version mismatch, a size the other build will not
            // take - arrived as a dead connection halfway through the upload,
            // and the sentence explaining it was never read.
            var accepted = PortableProtocol.Deserialize<BugReportAck>(
                await PortableProtocol.ReadFrameAsync(connection.Stream, timeout.Token).ConfigureAwait(false),
                JsonOptions);
            if (accepted is null || !accepted.Ok)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(accepted?.Message)
                        ? "Получатель не принял отчёт."
                        : accepted.Message);
            }

            await using (var archive = File.OpenRead(archivePath))
            {
                // Copied by hand rather than with CopyToAsync so the window can
                // say how much of it has gone: a report is megabytes over a
                // Steam relay, and silence for a minute reads as a hang.
                var buffer = new byte[SendChunkBytes];
                var sent = 0L;
                progress?.Report(new BugReportProgress(0, info.Length));
                while (true)
                {
                    var read = await archive.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await connection.Stream.WriteAsync(buffer.AsMemory(0, read), timeout.Token)
                        .ConfigureAwait(false);
                    sent += read;
                    progress?.Report(new BugReportProgress(sent, info.Length));
                }
            }
            await connection.Stream.FlushAsync(timeout.Token).ConfigureAwait(false);

            var ack = PortableProtocol.Deserialize<BugReportAck>(
                await PortableProtocol.ReadFrameAsync(connection.Stream, timeout.Token).ConfigureAwait(false),
                JsonOptions);
            if (ack is null || !ack.Ok)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(ack?.Message)
                        ? "Получатель не принял отчёт."
                        : ack.Message);
            }

            _logger.Info($"Bug report {reportId} ({info.Length / 1024} KiB) sent to {recipient}.");
            return manifest;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>Receives a report from a friend and files it under Personal/BugReports.</summary>
    async Task IPortableProtocolHandler.HandleAsync(
        Stream stream,
        byte[] initialFrame,
        PeerConnectionContext context,
        CancellationToken token)
    {
        var manifest = PortableProtocol.Deserialize<BugReportManifest>(initialFrame, JsonOptions)
            ?? throw new InvalidDataException("The bug report manifest is empty.");

        try
        {
            ValidateManifest(manifest, context);
            var directory = PrepareReportDirectory(manifest, context);
            var archivePath = Path.Combine(directory, "report.zip");
            await PortableProtocol.WriteJsonAsync(
                stream,
                new BugReportAck { Ok = true, Stage = "Accepted", ReportId = manifest.ReportId },
                JsonOptions,
                token).ConfigureAwait(false);
            await ReceiveArchiveAsync(stream, archivePath, manifest, token).ConfigureAwait(false);
            ExtractArchive(archivePath, directory);
            WriteReadme(directory, manifest, context);
            File.Delete(archivePath);

            await PortableProtocol.WriteJsonAsync(
                stream,
                new BugReportAck { Ok = true, Stage = "Stored", ReportId = manifest.ReportId },
                JsonOptions,
                token).ConfigureAwait(false);
            _logger.Info($"Bug report {manifest.ReportId} received from {context.PeerId} into {directory}.");
            PruneStoredReports();
            ReportReceived?.Invoke(this, new BugReportArrival(
                directory,
                string.IsNullOrWhiteSpace(manifest.SenderPlayerName)
                    ? manifest.SenderPersonaName
                    : manifest.SenderPlayerName,
                manifest.ArchiveBytes));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException)
        {
            _logger.Warn($"Bug report from {context.PeerId} was rejected: {ex.Message}");
            try
            {
                await PortableProtocol.WriteJsonAsync(
                    stream,
                    new BugReportAck
                    {
                        Ok = false,
                        Stage = "Rejected",
                        ReportId = manifest.ReportId,
                        Message = ex.Message
                    },
                    JsonOptions,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            throw;
        }
    }

    /// <summary>
    /// The files worth sending: the launcher's own log, the game's current and
    /// previous session, and any crash report from the last day. Each is
    /// sanitised and tail-trimmed, because what explains a failure is its end.
    /// </summary>
    /// <summary>
    /// The machine as it is right now - versions, mods, runtime state. It is
    /// what turns "it crashed" into something another person can act on.
    /// </summary>
    private async Task<string?> CaptureEnvironmentAsync(CancellationToken token)
    {
        if (_environmentProvider is null) return null;
        try
        {
            var snapshot = await _environmentProvider(token).ConfigureAwait(false);
            return JsonSerializer.Serialize(snapshot, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            _logger.Warn($"Bug report environment could not be captured: {ex.Message}");
            return null;
        }
    }

    private List<string> BuildArchive(
        string archivePath,
        BugReportContext context,
        string message,
        string? environmentJson)
    {
        var entries = new List<(string Name, string Path, int MaxBytes)>();
        if (File.Exists(_paths.LogFile))
        {
            entries.Add(("launcher/logs.log", _paths.LogFile, MaxLiveLogTailBytes));
        }

        var instance = _instanceDirectoryProvider();
        if (!string.IsNullOrWhiteSpace(instance) && Directory.Exists(instance))
        {
            var logs = Path.Combine(instance, "logs");
            if (Directory.Exists(logs))
            {
                var latest = Path.Combine(logs, "latest.log");
                if (File.Exists(latest))
                {
                    entries.Add(("game/latest.log", latest, MaxLiveLogTailBytes));
                }
                foreach (var archived in Directory.EnumerateFiles(logs, "*.log.gz")
                             .Select(path => new FileInfo(path))
                             .Where(file => file.LastWriteTimeUtc > _timeProvider.GetUtcNow().AddDays(-1))
                             .OrderByDescending(file => file.LastWriteTimeUtc)
                             .Take(2))
                {
                    // Named without the .gz: what lands in the report is the
                    // text inside it, unpacked here rather than by the reader.
                    var unpacked = archived.Name[..^".gz".Length];
                    entries.Add(($"game/{unpacked}", archived.FullName, MaxSupportingLogTailBytes));
                }
            }

            var crashes = Path.Combine(instance, "crash-reports");
            if (Directory.Exists(crashes))
            {
                foreach (var crash in Directory.EnumerateFiles(crashes, "*.txt")
                             .Select(path => new FileInfo(path))
                             .Where(file => file.LastWriteTimeUtc > _timeProvider.GetUtcNow().AddDays(-1))
                             .OrderByDescending(file => file.LastWriteTimeUtc)
                             .Take(3))
                {
                    entries.Add(($"crash-reports/{crash.Name}", crash.FullName, MaxSupportingLogTailBytes));
                }
            }
        }

        var written = new List<string>();
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteTextEntry(zip, "report.txt", BuildReportText(context, message));
            if (environmentJson is not null)
            {
                WriteTextEntry(zip, "environment.json", environmentJson);
                written.Add("environment.json");
            }
            foreach (var (name, path, maxBytes) in entries)
            {
                try
                {
                    var text = ReadSanitizedTail(path, maxBytes);
                    if (text.Length == 0) continue;
                    WriteTextEntry(zip, name, text);
                    written.Add(name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Warn($"Bug report could not include {name}: {ex.Message}");
                }
            }
        }

        var size = new FileInfo(archivePath).Length;
        if (size > MaxArchiveBytes)
        {
            throw new InvalidOperationException(
                $"Отчёт получился слишком большим ({size / (1024 * 1024)} МиБ). Сообщите об этом вручную.");
        }
        return written;
    }

    private string BuildReportText(BugReportContext context, string message) =>
        new StringBuilder()
            .AppendLine("LANMinecraft bug report")
            .AppendLine(CultureInfo.InvariantCulture, $"created: {_timeProvider.GetUtcNow():O}")
            .AppendLine(CultureInfo.InvariantCulture, $"player: {context.PlayerName} ({context.PersonaName})")
            .AppendLine(CultureInfo.InvariantCulture, $"steam: {context.SteamId64}")
            .AppendLine(CultureInfo.InvariantCulture, $"minecraft uuid: {context.MinecraftUuid}")
            .AppendLine(CultureInfo.InvariantCulture, $"launcher: {context.LauncherVersion}")
            .AppendLine(CultureInfo.InvariantCulture, $"pack: {context.PackName} ({context.PackHash})")
            .AppendLine(CultureInfo.InvariantCulture, $"game running: {context.IsMinecraftRunning}")
            .AppendLine()
            .AppendLine("--- what the player wrote ---")
            .AppendLine(NormalizeMessage(message))
            .ToString();

    /// <summary>
    /// The end of a log, sanitised. A crash is explained by the last pages, and
    /// a whole modded session is megabytes of startup nobody reads.
    /// </summary>
    private string ReadSanitizedTail(string path, int maxBytes)
    {
        using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // The game keeps yesterday's sessions gzipped. Reading those bytes as
        // text turns every byte outside ASCII into a replacement character, and
        // what the report then carries is not the log but its wreckage: the
        // archive has to be unpacked before a single line can be read.
        Stream stream = file;
        var truncated = false;
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            stream = Unpack(file, maxBytes, out truncated);
        }
        else
        {
            truncated = file.Length > maxBytes;
            if (truncated) file.Seek(-maxBytes, SeekOrigin.End);
        }
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var builder = new StringBuilder();
        // The first line after a seek is almost always half a line.
        if (truncated) reader.ReadLine();
        while (reader.ReadLine() is { } line)
        {
            var sanitized = _sanitizer.SanitizeLine(line);
            if (sanitized is not null) builder.AppendLine(sanitized);
        }
        return builder.ToString();
    }

    /// <summary>
    /// The tail of a gzipped log, as bytes. Only the last <paramref name="maxBytes"/>
    /// are kept, and the stream is read through a window rather than into memory,
    /// so a log that grew to gigabytes costs the same as one that did not.
    /// </summary>
    private static Stream Unpack(Stream file, int maxBytes, out bool truncated)
    {
        using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
        var window = new byte[maxBytes];
        var filled = 0;
        var total = 0L;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (read >= window.Length)
            {
                Buffer.BlockCopy(buffer, read - window.Length, window, 0, window.Length);
                filled = window.Length;
                continue;
            }
            if (filled + read > window.Length)
            {
                var keep = window.Length - read;
                Buffer.BlockCopy(window, filled - keep, window, 0, keep);
                filled = keep;
            }
            Buffer.BlockCopy(buffer, 0, window, filled, read);
            filled += read;
        }
        truncated = total > filled;
        return new MemoryStream(window, 0, filled, writable: false);
    }

    private static void WriteTextEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private void ValidateManifest(BugReportManifest manifest, PeerConnectionContext context)
    {
        if (!string.Equals(manifest.Protocol, BugReportManifest.ProtocolName, StringComparison.Ordinal) ||
            !PortableFormat.CanSpeak(manifest.Version))
        {
            throw new InvalidDataException(
                "Отчёт отправлен более новой версией лаунчера. Обновите лаунчер, чтобы его прочитать.");
        }
        if (!IsSafeReportId(manifest.ReportId))
        {
            throw new InvalidDataException("The bug report id is not a plain identifier.");
        }
        if (!string.Equals(manifest.SenderSteamId64, context.PeerId.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The bug report sender does not match its Steam connection.");
        }
        if (manifest.ArchiveBytes is <= 0 or > MaxArchiveBytes)
        {
            throw new InvalidDataException("The bug report archive size is not plausible.");
        }
        if (manifest.ArchiveSha256.Length != 64)
        {
            throw new InvalidDataException("The bug report archive hash is malformed.");
        }
    }

    private string PrepareReportDirectory(BugReportManifest manifest, PeerConnectionContext context)
    {
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"{manifest.CreatedAtUtc.ToLocalTime():yyyyMMdd-HHmmss}-{context.PeerId}-{manifest.ReportId[..8]}");
        var directory = Path.Combine(ReportsDirectory, name);
        _paths.EnsureUnderRoot(directory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task ReceiveArchiveAsync(
        Stream stream,
        string archivePath,
        BugReportManifest manifest,
        CancellationToken token)
    {
        await using (var file = File.Create(archivePath))
        {
            var buffer = new byte[64 * 1024];
            var remaining = manifest.ArchiveBytes;
            while (remaining > 0)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token)
                    .ConfigureAwait(false);
                if (read == 0) throw new InvalidDataException("The bug report ended early.");
                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                remaining -= read;
            }
        }

        if (!string.Equals(HashFile(archivePath), manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The bug report archive hash does not match.");
        }
    }

    /// <summary>
    /// Extraction refuses anything that climbs out of the report directory -
    /// the archive came from another machine, and only its own contents may
    /// land on this one.
    /// </summary>
    private static void ExtractArchive(string archivePath, string directory)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var target = Path.GetFullPath(Path.Combine(directory, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The bug report archive escapes its directory.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// A short header so whoever opens the folder - or points a tool at it -
    /// sees what happened without reading the logs first.
    /// </summary>
    private void WriteReadme(string directory, BugReportManifest manifest, PeerConnectionContext context)
    {
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"# Bug report from {manifest.SenderPlayerName}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"- Steam: {context.PersonaName} ({context.PeerId})")
            .AppendLine(CultureInfo.InvariantCulture, $"- Sent: {manifest.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}")
            .AppendLine(CultureInfo.InvariantCulture, $"- Launcher: {manifest.LauncherVersion}")
            .AppendLine(CultureInfo.InvariantCulture, $"- Pack: {manifest.PackName} ({manifest.PackHash})")
            .AppendLine()
            .AppendLine("## What the player wrote")
            .AppendLine()
            .AppendLine(string.IsNullOrWhiteSpace(manifest.Message) ? "(nothing)" : manifest.Message)
            .AppendLine()
            .AppendLine("## Files")
            .AppendLine()
            .AppendLine("- `report.txt` - the same message plus the sender's environment")
            .AppendLine("- `launcher/logs.log` - the launcher's own log, sanitised")
            .AppendLine("- `game/latest.log` - the game session, sanitised and trimmed to its end")
            .AppendLine("- `crash-reports/` - crash reports from the last day, if any")
            .ToString();
        File.WriteAllText(Path.Combine(directory, "README.md"), text, new UTF8Encoding(false));
    }

    /// <summary>Reports are diagnostics: recent ones are useful, old ones are clutter.</summary>
    public void PruneStoredReports()
    {
        if (!Directory.Exists(ReportsDirectory)) return;
        try
        {
            var directories = Directory.GetDirectories(ReportsDirectory)
                .Select(path => new DirectoryInfo(path))
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .ToArray();
            var cutoff = _timeProvider.GetUtcNow().UtcDateTime - Retention;
            var used = 0L;
            foreach (var directory in directories)
            {
                var size = DirectorySize(directory);
                used += size;
                if (directory.LastWriteTimeUtc < cutoff || used > MaxStoredBytes)
                {
                    TryDeleteDirectory(directory.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static long DirectorySize(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string NormalizeMessage(string? message)
    {
        var trimmed = (message ?? string.Empty).Trim();
        return trimmed.Length <= MaxMessageCharacters ? trimmed : trimmed[..MaxMessageCharacters];
    }

    private static bool IsSafeReportId(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length is 32 &&
        value.All(char.IsAsciiLetterOrDigit);

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>What the launcher knows about itself when a report is written.</summary>
/// <summary>How much of a report has left this machine, and how much there is.</summary>
public sealed record BugReportProgress(long SentBytes, long TotalBytes);

/// <summary>A report that just arrived: where it is, who sent it, how big it was.</summary>
public sealed record BugReportArrival(string Directory, string SenderName, long ArchiveBytes);

public sealed record BugReportContext(
    SteamId64 SteamId64,
    string PersonaName,
    string PlayerName,
    string MinecraftUuid,
    string LauncherVersion,
    string PackName,
    string PackHash,
    bool IsMinecraftRunning);
