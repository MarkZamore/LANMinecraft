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
/// path our bytes take is the peer's connection quality; this reads it every
/// few seconds and moves the floor: sharply down while packets are being lost,
/// gently up while they are not.
///
/// SendRateMin and SendRateMax are never locked, so they can be changed on a
/// live connection - the one place Steam lets a sender adapt.
/// </summary>
internal sealed class SendRateGovernor
{
    internal const int MinimumBytesPerSecond = 256 * 1024;
    internal const int MaximumBytesPerSecond = 100 * 1024 * 1024;
    internal const int InitialBytesPerSecond = 8 * 1024 * 1024;

    /// <summary>Below this share of packets arriving, the path is being flooded.</summary>
    internal const float LossyQuality = 0.9f;

    /// <summary>Above this, the path has room to spare.</summary>
    internal const float CleanQuality = 0.98f;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(3);

    private readonly HSteamNetConnection _connection;
    private long _nextSampleAt;
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

        var next = Decide(_rate, quality);
        if (next == _rate) return;
        if (Apply(next)) Volatile.Write(ref _rate, next);
    }

    /// <summary>
    /// The control law on its own, so it can be reasoned about without Steam.
    /// Halving on loss and adding a step on a clean read is the same shape TCP
    /// settled on: it converges to the path's capacity and backs off before it
    /// makes things worse.
    /// </summary>
    internal static int Decide(int current, float remoteQuality)
    {
        if (remoteQuality < LossyQuality)
        {
            return Math.Max(MinimumBytesPerSecond, current / 2);
        }
        if (remoteQuality >= CleanQuality)
        {
            var step = Math.Max(256 * 1024, current / 4);
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
