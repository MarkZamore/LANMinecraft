using System.Diagnostics;
using System.Runtime.InteropServices;
using Steamworks;

namespace Minecraft;

/// <summary>
/// Keeps a connection's send rate at what its path can actually carry.
///
/// Steam has no rate estimator of its own: it computes a permitted rate once
/// at connect from the ping and clamps it between SendRateMin and SendRateMax,
/// so the floor this launcher sets is the pace every transfer runs at. A fixed
/// 8 MiB/s floor into a relay that carries two drowned it in resends - the
/// remote side reported 15 % of our packets arriving and the world crawled at
/// 0.05 MiB/s while the wire showed 8. The only measurement Steam gives of the
/// path our bytes take is the peer's connection quality; this reads it and
/// moves the floor: down when packets are being lost, up while they are not.
///
/// SendRateMin and SendRateMax are never locked, so they can be changed on a
/// live connection - the one place Steam lets a sender adapt.
/// </summary>
internal sealed class SendRateGovernor
{
    internal const int MinimumBytesPerSecond = 256 * 1024;
    internal const int MaximumBytesPerSecond = 100 * 1024 * 1024;
    internal const int InitialBytesPerSecond = 4 * 1024 * 1024;

    /// <summary>Below this share of packets arriving, the path is being flooded.</summary>
    internal const float LossyQuality = 0.9f;

    /// <summary>Above this, the path has room to spare.</summary>
    internal const float CleanQuality = 0.98f;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The peer's quality figure is a running average that outlives the loss
    /// that lowered it. Cutting on every read while it recovers means cutting
    /// three or four times for one event, all the way to the floor - the first
    /// version of this did exactly that and pinned a 1.3 MiB/s path at 0.25.
    /// One cut per event, then silence until the number has had time to move.
    /// </summary>
    private static readonly TimeSpan BackoffHold = TimeSpan.FromSeconds(12);

    private readonly HSteamNetConnection _connection;
    private long _nextSampleAt;
    private long _holdUntil;
    private float _lastQuality = -1f;
    private int _rate = InitialBytesPerSecond;

    internal SendRateGovernor(HSteamNetConnection connection)
    {
        _connection = connection;
    }

    internal int RateBytesPerSecond => Volatile.Read(ref _rate);

    /// <summary>
    /// Called from the send loop; cheap enough to call per message because it
    /// only acts once per <see cref="SampleInterval"/>.
    /// </summary>
    internal void Observe()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < _nextSampleAt) return;
        _nextSampleAt = now + (long)(SampleInterval.TotalSeconds * Stopwatch.Frequency);

        var status = default(SteamNetConnectionRealTimeStatus_t);
        var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
        if (SteamNetworkingSockets.GetConnectionRealTimeStatus(_connection, ref status, 0, ref lanes) !=
            EResult.k_EResultOK)
        {
            return;
        }

        // The remote quality is what the peer saw of our packets over the last
        // stretch; a value below zero means Steam has not measured it yet.
        var quality = status.m_flConnectionQualityRemote;
        if (quality < 0) return;

        var holding = now < _holdUntil;
        var next = Decide(_rate, quality, _lastQuality, holding);
        _lastQuality = quality;
        if (next < _rate)
        {
            _holdUntil = now + (long)(BackoffHold.TotalSeconds * Stopwatch.Frequency);
        }
        if (next == _rate) return;
        if (Apply(next)) Volatile.Write(ref _rate, next);
    }

    /// <summary>
    /// The control law on its own, so it can be reasoned about without Steam.
    /// A loss event backs off by a third - enough to stop the flooding, not
    /// enough to throw the capacity away - and is acted on once: while
    /// <paramref name="holding"/> the stale average is ignored, and a reading
    /// that is merely still low but no longer falling is not a new event.
    /// A clean path grows by an eighth per read, so a run of clean reads finds
    /// the ceiling in under a minute without overshooting it by a multiple.
    /// </summary>
    internal static int Decide(int current, float remoteQuality, float previousQuality, bool holding)
    {
        if (remoteQuality < LossyQuality)
        {
            var newEvent = !holding && (previousQuality < 0 || remoteQuality <= previousQuality);
            return newEvent
                ? Math.Max(MinimumBytesPerSecond, current - current / 3)
                : current;
        }
        if (remoteQuality >= CleanQuality && !holding)
        {
            var step = Math.Max(128 * 1024, current / 8);
            return (int)Math.Min(MaximumBytesPerSecond, (long)current + step);
        }
        return current;
    }

    private bool Apply(int bytesPerSecond)
    {
        var handle = GCHandle.Alloc(bytesPerSecond, GCHandleType.Pinned);
        try
        {
            var target = (IntPtr)_connection.m_HSteamNetConnection;
            var min = SteamNetworkingUtils.SetConfigValue(
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection,
                target,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                handle.AddrOfPinnedObject());
            var max = SteamNetworkingUtils.SetConfigValue(
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection,
                target,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                handle.AddrOfPinnedObject());
            return min && max;
        }
        finally
        {
            handle.Free();
        }
    }
}
