using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minecraft;

internal sealed record PackTransferOffer(SteamId64 Sender, string SenderName, string PackName, long ArchiveBytes);

internal enum PackTransferPhase { Preparing, AwaitingConfirmation, Sending, Receiving, Installing, Completed, Idle }

internal sealed record PackTransferProgress(PackTransferPhase Phase, double Fraction);

/// <summary>Transfers a personal pack over an authenticated Steam friend connection.</summary>
internal sealed class PackTransferService(AppPaths paths, IPeerTransport transport, PackArchiveService archives,
    Func<PackTransferOffer, CancellationToken, Task<bool>> confirm, Func<bool>? mayStart = null,
    Logger? logger = null) : IPortableProtocolHandler, IAsyncDisposable
{
    internal const string Protocol = "MinecraftPortablePack";
    private const int FrameLimit = 4096;
    private const int BufferSize = 128 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InstallationTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(150);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true
    };

    private readonly object _gate = new();
    private ActiveTransfer? _active;
    private bool _disposed;

    internal event Action<PackTransferProgress>? ProgressChanged;
    internal event Action<string>? PackReceived;
    internal event Action<string>? IncomingFailed;
    string IPortableProtocolHandler.ProtocolName => Protocol;

    internal bool IsActive
    {
        get { lock (_gate) return _active is not null; }
    }

    internal async Task SendAsync(PeerViewModel peer, string packRelativePath, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (!peer.SteamId.IsValid) throw new ArgumentException("Выберите игрока Steam.", nameof(peer));
        if (!transport.IsAvailable) throw new InvalidOperationException(transport.UnavailableReason);
        var operation = Begin(token) ?? throw new InvalidOperationException("Уже выполняется передача сборки.");
        try
        {
            token = operation.Cancellation.Token;
            token.ThrowIfCancellationRequested();
            if (mayStart?.Invoke() == false)
                throw new InvalidOperationException("Завершите текущую операцию перед передачей сборки.");
            Report(PackTransferPhase.Preparing, 0);
            using var lease = PortablePackSyncService.CreateTransferStage(paths, out var stage);
            var path = Path.Combine(stage, "outgoing" + PackArchiveService.Extension);
            await archives.ExportAsync(packRelativePath, path,
                CreateProgress(PackTransferPhase.Preparing), token).ConfigureAwait(false);
            await using var archive = OpenArchive(path, writing: false);
            var bytes = archive.Length;
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(archive, token).ConfigureAwait(false));
            archive.Position = 0;
            var transferId = Guid.NewGuid();
            await using var connection = await transport.ConnectAsync(peer.SteamId, Protocol, token).ConfigureAwait(false);
            if (connection.Context.PeerId != peer.SteamId || !connection.Context.IsFriend)
                throw new IOException("Не удалось подтвердить получателя в Steam.");
            var stream = connection.Stream;
            var header = new Header(Protocol, PortableFormat.ProtocolVersion, transferId, packRelativePath, bytes, digest);
            await WriteFrameAsync(stream, header, token).ConfigureAwait(false);
            Report(PackTransferPhase.AwaitingConfirmation, 0);
            var accepted = await ReadReplyAsync(stream, transferId, ConfirmationTimeout, token).ConfigureAwait(false);
            RequireStatus(accepted, "Accepted");

            Report(PackTransferPhase.Sending, 0);
            await CopyArchiveAsync(archive, stream, bytes, PackTransferPhase.Sending, null, token).ConfigureAwait(false);
            await WriteReplyAsync(stream, transferId, "Sent", token).ConfigureAwait(false);
            var stored = await ReadReplyAsync(stream, transferId, InstallationTimeout, token).ConfigureAwait(false);
            RequireStatus(stored, "Stored");
            Report(PackTransferPhase.Completed, 1);
        }
        catch (EndOfStreamException ex)
        {
            throw new IOException("Связь прервалась до подтверждения. Проверьте, что у друга открыт обновлённый лаунчер.", ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or TimeoutException or NotSupportedException)
        {
            throw new IOException(ex is JsonException ? "Ответ игрока о передаче сборки повреждён." : ex.Message, ex);
        }
        finally { End(operation); }
    }

    async Task IPortableProtocolHandler.HandleAsync(Stream stream, byte[] initialFrame,
        PeerConnectionContext context, CancellationToken token)
    {
        if (!context.IsFriend || !context.PeerId.IsValid || initialFrame.Length > FrameLimit) return;
        var header = PortableProtocol.Deserialize<Header>(initialFrame, JsonOptions);
        if (header is null || !MatchesProtocol(header.Protocol, header.ProtocolVersion, header.TransferId) ||
            header.PackName is not { Length: > 0 and <= 255 } ||
            header.Bytes is <= 0 or > PackArchiveService.MaxArchiveBytes ||
            header.Sha256 is not { Length: 64 } || !header.Sha256.All(Uri.IsHexDigit))
            return;

        var operation = Begin(token);
        if (operation is null)
        {
            await WriteReplyAsync(stream, header.TransferId, "Busy", token).ConfigureAwait(false);
            return;
        }
        try
        {
            token = operation.Cancellation.Token;
            if (mayStart?.Invoke() == false)
            {
                await WriteReplyAsync(stream, header.TransferId, "Busy", token).ConfigureAwait(false);
                return;
            }
            try { archives.CheckCanImport(header.PackName); }
            catch (InvalidDataException)
            {
                await WriteReplyAsync(stream, header.TransferId, "Failed", token).ConfigureAwait(false);
                return;
            }
            var drive = new DriveInfo(Path.GetPathRoot(paths.Launcher)!);
            if (drive.AvailableFreeSpace < header.Bytes)
            {
                await WriteReplyAsync(stream, header.TransferId, "NoSpace", token).ConfigureAwait(false);
                return;
            }
            Report(PackTransferPhase.AwaitingConfirmation, 0);
            var senderName = string.IsNullOrWhiteSpace(context.PersonaName)
                ? context.PeerId.ToString() : context.PersonaName;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(ConfirmationTimeout);
                var offer = new PackTransferOffer(context.PeerId, senderName, header.PackName, header.Bytes);
                if (!await confirm(offer, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false))
                {
                    await WriteReplyAsync(stream, header.TransferId, "Declined", token).ConfigureAwait(false);
                    return;
                }
            }

            using var lease = PortablePackSyncService.CreateTransferStage(paths, out var stage);
            var path = Path.Combine(stage, "incoming" + PackArchiveService.Extension);
            await using (var archive = OpenArchive(path, writing: true))
            {
                await WriteReplyAsync(stream, header.TransferId, "Accepted", token).ConfigureAwait(false);
                Report(PackTransferPhase.Receiving, 0);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await CopyArchiveAsync(stream, archive, header.Bytes, PackTransferPhase.Receiving, hash, token)
                    .ConfigureAwait(false);
                var sent = await ReadReplyAsync(stream, header.TransferId, IdleTimeout, token).ConfigureAwait(false);
                RequireStatus(sent, "Sent");
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(header.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Контрольная сумма полученного архива сборки не совпала.");
            }

            Report(PackTransferPhase.Installing, 0);
            string installed;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(InstallationTimeout);
                installed = await archives.ImportAsync(path, CreateProgress(PackTransferPhase.Installing), deadline.Token,
                    header.PackName).ConfigureAwait(false);
            }
            PackReceived?.Invoke(installed);
            await WriteReplyAsync(stream, header.TransferId, "Stored", token).ConfigureAwait(false);
            Report(PackTransferPhase.Completed, 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   JsonException or InvalidOperationException or TimeoutException or ArgumentException or NotSupportedException ||
                                   ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            logger?.Warn("Pack transfer failed: " + ex.Message);
            if (ex is not OperationCanceledException)
                IncomingFailed?.Invoke("Не удалось получить сборку: " +
                    (ex is JsonException ? "повреждено подтверждение отправителя." : ex.Message));
            try { await WriteReplyAsync(stream, header.TransferId, "Failed", token).ConfigureAwait(false); }
            catch (Exception failure) when (failure is IOException or InvalidOperationException or
                                             OperationCanceledException or TimeoutException) { }
        }
        finally { End(operation); }
    }

    private async Task CopyArchiveAsync(Stream input, Stream output, long size, PackTransferPhase phase,
        IncrementalHash? hash, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        var buffer = new byte[BufferSize];
        var queued = output as IQueuedByteSink;
        var copied = 0L;
        var reported = 0L;
        var sampledAt = Stopwatch.GetTimestamp();
        try
        {
            while (copied < size)
            {
                idle.CancelAfter(IdleTimeout);
                var count = (int)Math.Min(size - copied, buffer.Length);
                var read = await input.ReadAsync(buffer.AsMemory(0, count), idle.Token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Передача архива сборки прервалась.");
                hash?.AppendData(buffer, 0, read);
                if (queued is not null)
                {
                    var write = output.WriteAsync(buffer.AsMemory(0, read), idle.Token).AsTask();
                    while (!idle.IsCancellationRequested &&
                           await Task.WhenAny(write, Task.Delay(ProgressInterval, idle.Token)).ConfigureAwait(false) != write)
                    {
                        ReportDelivered(copied - queued.QueuedBytes);
                    }
                    await write.ConfigureAwait(false);
                }
                else
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
                }
                copied += read;
                ReportDelivered(copied - (queued?.QueuedBytes ?? 0));
            }
            idle.CancelAfter(IdleTimeout);
            await output.FlushAsync(idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Передача сборки остановилась.");
        }

        void ReportDelivered(long delivered)
        {
            if (delivered <= reported || delivered < size && Stopwatch.GetElapsedTime(sampledAt) < ProgressInterval)
                return;
            sampledAt = Stopwatch.GetTimestamp();
            reported = Math.Min(delivered, size);
            Report(phase, reported / (double)size);
        }
    }

    private static async Task<Reply> ReadReplyAsync(Stream stream, Guid transferId, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            var body = await PortableProtocol.ReadFrameAsync(stream, deadline.Token, FrameLimit).ConfigureAwait(false);
            var reply = PortableProtocol.Deserialize<Reply>(body, JsonOptions);
            if (reply is null || !MatchesProtocol(reply.Protocol, reply.ProtocolVersion, reply.TransferId) ||
                reply.TransferId != transferId)
                throw new InvalidDataException("Подтверждение относится к другой передаче сборки.");
            return reply;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Игрок не подтвердил передачу сборки.");
        }
    }

    private static bool MatchesProtocol(string? protocol, int version, Guid transferId) =>
        protocol == Protocol && PortableFormat.CanSpeak(version) && transferId != Guid.Empty;

    private static void RequireStatus(Reply reply, string expected)
    {
        if (reply.Status == expected) return;
        var message = reply.Status switch
        {
            "Declined" => "Игрок отклонил передачу сборки.",
            "Busy" => "Получатель занят другой операцией.",
            "NoSpace" => "У получателя недостаточно места для архива сборки.",
            "Failed" => "Получатель не смог проверить или установить сборку.",
            _ => "Получено неожиданное подтверждение передачи сборки."
        };
        throw new InvalidOperationException(message);
    }

    private static Task WriteReplyAsync(Stream stream, Guid transferId, string status, CancellationToken token) =>
        WriteFrameAsync(stream, new Reply(Protocol, PortableFormat.ProtocolVersion, transferId, status), token);

    private static async Task WriteFrameAsync<T>(Stream stream, T frame, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(IdleTimeout);
        try { await PortableProtocol.WriteJsonAsync(stream, frame, JsonOptions, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Не удалось передать подтверждение по Steam.");
        }
    }

    private static FileStream OpenArchive(string path, bool writing) => new(path,
        writing ? FileMode.CreateNew : FileMode.Open, writing ? FileAccess.Write : FileAccess.Read,
        writing ? FileShare.None : FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private IProgress<double> CreateProgress(PackTransferPhase phase) =>
        new TransferProgress(fraction => Report(phase, fraction));

    private void Report(PackTransferPhase phase, double fraction) => ProgressChanged?.Invoke(new(phase, fraction));

    private ActiveTransfer? Begin(CancellationToken token)
    {
        lock (_gate)
        {
            if (_disposed || _active is not null) return null;
            _active = new ActiveTransfer(CancellationTokenSource.CreateLinkedTokenSource(token));
            return _active;
        }
    }

    private void End(ActiveTransfer operation)
    {
        lock (_gate)
        {
            _active = null;
            operation.Cancellation.Dispose();
            operation.Completion.TrySetResult();
            Report(PackTransferPhase.Idle, 0);
        }
    }

    internal void CancelActive()
    {
        lock (_gate) _active?.Cancellation.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Task? completion;
        lock (_gate)
        {
            _disposed = true;
            completion = _active?.Completion.Task;
            _active?.Cancellation.Cancel();
        }
        if (completion is not null) await completion.ConfigureAwait(false);
    }

    private sealed record ActiveTransfer(CancellationTokenSource Cancellation)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TransferProgress(Action<double> report) : IProgress<double>
    {
        private long _sampledAt;
        public void Report(double value)
        {
            if (value < 1 && Stopwatch.GetElapsedTime(_sampledAt) < ProgressInterval) return;
            _sampledAt = Stopwatch.GetTimestamp();
            report(value);
        }
    }

    private sealed record Header(string Protocol, int ProtocolVersion, Guid TransferId, string PackName, long Bytes, string Sha256);
    private sealed record Reply(string Protocol, int ProtocolVersion, Guid TransferId, string Status);
}
