package minecraft.portable.identity;

/** Records only the historical screen callback. This fixture starts no Steam worker. */
public final class PortableSteamDiscoveryHooks {
    public static int publications;

    public static void published(Object client, Object server, int port) {
        publications++;
    }
}
