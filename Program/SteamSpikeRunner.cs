using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Steamworks;

namespace Minecraft;

/// <summary>
/// Temporary console harness for the Steam transport spike (plan gate SPIKE 0).
/// It answers the questions that decide the transport design and cannot be
/// answered locally: can the launcher hold a Steam session while the game runs
/// e4steam under the same App ID, does a P2P connection between two launchers
/// establish, how fast is it, and do rich-presence keys survive e4steam.
///
/// Usage (both sides run the real single-file exe):
///   LANMinecraft.exe --steam-spike probe
///   LANMinecraft.exe --steam-spike listen
///   LANMinecraft.exe --steam-spike connect &lt;friendSteamId64&gt; [megabytes]
///   LANMinecraft.exe --steam-spike presence
///
/// This file is removed before the migration branch is merged.
/// </summary>
internal static class SteamSpikeRunner
{
    internal const string Argument = "--steam-spike";

    /// <summary>Virtual port the launcher-to-launcher transport will own.</summary>
    private const int LauncherVirtualPort = 35656;
    private const int ChunkBytes = 256 * 1024;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(45);

    private static HSteamListenSocket _listenSocket;
    private static HSteamNetConnection _incoming;
    private static HSteamNetConnection _outgoing;
    private static Callback<SteamNetConnectionStatusChangedCallback_t>? _statusCallback;
    private static StreamWriter? _transcript;

    public static bool TryRun(IReadOnlyList<string> arguments)
    {
        var index = arguments.ToList().FindIndex(argument =>
            string.Equals(argument, Argument, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;

        var mode = index + 1 < arguments.Count ? arguments[index + 1] : "probe";
        var rest = arguments.Skip(index + 2).ToArray();
        AllocConsole();
        Console.OutputEncoding = Encoding.UTF8;

        var paths = new AppPaths(AppPaths.ResolveApplicationRoot());
        paths.Ensure();
        var transcriptPath = Path.Combine(
            paths.Personal,
            $"steam-spike-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _transcript = new StreamWriter(transcriptPath, append: true) { AutoFlush = true };
        Log($"transcript: {transcriptPath}");

        var logger = new Logger(paths.LogFile);
        var native = new SteamNativeLibraryService(paths, logger);
        var api = new SteamworksApiFacade();

        try
        {
            native.Prepare();
            if (!api.Initialize(out var failureReason))
            {
                Log($"FAIL init: {failureReason} (steam running: {api.IsSteamRunning()})");
                return true;
            }

            api.InitRelayNetworkAccess();
            Log($"OK init: {api.GetPersonaName()} ({api.GetLocalSteamId()}), logged on: {api.IsLoggedOn()}");

            switch (mode.ToLowerInvariant())
            {
                case "listen":
                    RunListen(api);
                    break;
                case "connect":
                    RunConnect(api, rest);
                    break;
                case "presence":
                    RunPresence(api);
                    break;
                default:
                    RunProbe(api);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"FAIL {mode}: {ex}");
        }
        finally
        {
            CloseSockets();
            api.Shutdown();
            Log("done. Press Enter to close.");
            Console.ReadLine();
            _transcript?.Dispose();
        }

        return true;
    }

    private static void RunProbe(ISteamApiFacade api)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            api.RunCallbacks();
            var status = SteamNetworkingUtils.GetRelayNetworkStatus(out var details);
            Log($"relay: {status}, avail-network {details.m_eAvailNetworkConfig}, " +
                $"ping location {details.m_debugMsg}");
            if (status == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current) break;
            Thread.Sleep(1000);
        }

        foreach (var friend in api.GetFriends())
        {
            Log($"friend {friend.SteamId64} {friend.PersonaName} inApp480={friend.IsInSharedApp} lobby={friend.LobbyId}");
        }
    }

    private static void RunListen(ISteamApiFacade api)
    {
        RegisterStatusCallback();
        _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(LauncherVirtualPort, 0, null);
        Log($"listening on virtual port {LauncherVirtualPort}; waiting for a peer. Ctrl+C to stop.");

        var received = 0L;
        var started = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var buffer = new IntPtr[32];

        while (true)
        {
            api.RunCallbacks();
            if (_incoming != HSteamNetConnection.Invalid)
            {
                var count = SteamNetworkingSockets.ReceiveMessagesOnConnection(_incoming, buffer, buffer.Length);
                for (var index = 0; index < count; index++)
                {
                    var message = Marshal.PtrToStructure<SteamNetworkingMessage_t>(buffer[index]);
                    received += message.m_cbSize;
                    SteamNetworkingMessage_t.Release(buffer[index]);
                }

                if (started.Elapsed - lastReport > TimeSpan.FromSeconds(2))
                {
                    lastReport = started.Elapsed;
                    Log($"received {received / (1024.0 * 1024.0):F1} MB " +
                        $"({received / Math.Max(1.0, started.Elapsed.TotalSeconds) / (1024.0 * 1024.0):F2} MB/s) {DescribeRoute(_incoming)}");
                }
            }

            Thread.Sleep(1);
        }
    }

    private static void RunConnect(ISteamApiFacade api, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || !ulong.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var peer))
        {
            Log("usage: --steam-spike connect <friendSteamId64> [megabytes]");
            return;
        }

        var megabytes = arguments.Count > 1 &&
                        int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 64;

        RegisterStatusCallback();
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID64(peer);
        _outgoing = SteamNetworkingSockets.ConnectP2P(ref identity, LauncherVirtualPort, 0, null);
        Log($"connecting to {peer} …");

        var deadline = DateTimeOffset.UtcNow + StepTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            api.RunCallbacks();
            if (SteamNetworkingSockets.GetConnectionInfo(_outgoing, out var info) &&
                info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
            {
                Log($"connected: {DescribeRoute(_outgoing)}");
                SendPayload(api, megabytes);
                return;
            }
            Thread.Sleep(50);
        }

        Log("FAIL: connection did not reach Connected within the timeout.");
    }

    private static void SendPayload(ISteamApiFacade api, int megabytes)
    {
        var payload = new byte[ChunkBytes];
        Random.Shared.NextBytes(payload);
        var handle = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            var total = (long)megabytes * 1024 * 1024;
            var sent = 0L;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            while (sent < total)
            {
                var result = SteamNetworkingSockets.SendMessageToConnection(
                    _outgoing,
                    handle.AddrOfPinnedObject(),
                    (uint)payload.Length,
                    Constants.k_nSteamNetworkingSend_Reliable,
                    out _);

                if (result == EResult.k_EResultOK)
                {
                    sent += payload.Length;
                }
                else if (result == EResult.k_EResultLimitExceeded)
                {
                    api.RunCallbacks();
                    Thread.Sleep(2);
                    continue;
                }
                else
                {
                    Log($"FAIL send: {result} after {sent / (1024.0 * 1024.0):F1} MB");
                    return;
                }

                api.RunCallbacks();
                if (clock.Elapsed - lastReport > TimeSpan.FromSeconds(2))
                {
                    lastReport = clock.Elapsed;
                    Log($"sent {sent / (1024.0 * 1024.0):F1} MB " +
                        $"({sent / clock.Elapsed.TotalSeconds / (1024.0 * 1024.0):F2} MB/s) {DescribeRoute(_outgoing)}");
                }
            }

            SteamNetworkingSockets.FlushMessagesOnConnection(_outgoing);
            var seconds = clock.Elapsed.TotalSeconds;
            Log($"OK sent {megabytes} MB in {seconds:F1} s = {megabytes / seconds:F2} MB/s {DescribeRoute(_outgoing)}");
        }
        finally
        {
            handle.Free();
        }
    }

    private static void RunPresence(ISteamApiFacade api)
    {
        api.SetRichPresence("lanmc", "1");
        api.SetRichPresence("lanmc_v", "1");
        api.SetRichPresence("lanmc_name", api.GetPersonaName());
        Log("published lanmc presence keys; watching friends for 60 s.");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            api.RunCallbacks();
            foreach (var friend in api.GetFriends())
            {
                api.RequestFriendRichPresence(friend.SteamId64);
                var marker = api.GetFriendRichPresence(friend.SteamId64, "lanmc");
                if (!string.IsNullOrEmpty(marker))
                {
                    Log($"peer {friend.SteamId64} {friend.PersonaName} " +
                        $"lanmc={marker} name={api.GetFriendRichPresence(friend.SteamId64, "lanmc_name")}");
                }
            }
            Thread.Sleep(3000);
        }
    }

    private static void RegisterStatusCallback()
    {
        _statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
    }

    private static void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t change)
    {
        Log($"connection {change.m_hConn.m_HSteamNetConnection} -> {change.m_info.m_eState} " +
            $"(peer {change.m_info.m_identityRemote.GetSteamID64()}, {change.m_info.m_szEndDebug})");

        switch (change.m_info.m_eState)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting
                when change.m_info.m_hListenSocket != HSteamListenSocket.Invalid:
                SteamNetworkingSockets.AcceptConnection(change.m_hConn);
                _incoming = change.m_hConn;
                Log($"accepted {change.m_info.m_identityRemote.GetSteamID64()}");
                break;
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                SteamNetworkingSockets.CloseConnection(change.m_hConn, 0, null, false);
                if (change.m_hConn == _incoming) _incoming = HSteamNetConnection.Invalid;
                if (change.m_hConn == _outgoing) _outgoing = HSteamNetConnection.Invalid;
                break;
        }
    }

    private static string DescribeRoute(HSteamNetConnection connection)
    {
        if (connection == HSteamNetConnection.Invalid) return "(no connection)";

        var status = default(SteamNetConnectionRealTimeStatus_t);
        var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
        SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lanes);
        var route = SteamNetworkingSockets.GetConnectionInfo(connection, out var info)
            ? (info.m_idPOPRelay.m_SteamNetworkingPOPID == 0 ? "direct" : $"relay {info.m_idPOPRelay.m_SteamNetworkingPOPID}")
            : "unknown";
        return $"[{route}, ping {status.m_nPing} ms, quality {status.m_flConnectionQualityLocal:F2}, " +
               $"pending {status.m_cbPendingReliable} B, sendRate {status.m_nSendRateBytesPerSecond / 1024} KB/s]";
    }

    private static void CloseSockets()
    {
        if (_incoming != HSteamNetConnection.Invalid)
        {
            SteamNetworkingSockets.CloseConnection(_incoming, 0, "spike over", false);
        }
        if (_outgoing != HSteamNetConnection.Invalid)
        {
            SteamNetworkingSockets.CloseConnection(_outgoing, 0, "spike over", false);
        }
        if (_listenSocket != HSteamListenSocket.Invalid)
        {
            SteamNetworkingSockets.CloseListenSocket(_listenSocket);
        }
        _statusCallback?.Dispose();
    }

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Console.WriteLine(line);
        _transcript?.WriteLine(line);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}
