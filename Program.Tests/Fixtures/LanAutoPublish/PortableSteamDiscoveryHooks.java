package minecraft.portable.identity;

import java.util.concurrent.CompletableFuture;

/** Records discovery and report actions without starting a Steam worker. */
public final class PortableSteamDiscoveryHooks {
    public static int publications;
    public static int recentPlayers;

    public static void published(Object client, Object server, int port) {
        publications++;
    }

    public static CompletableFuture<Void> openRecentPlayers(ClassLoader loader) {
        recentPlayers++;
        return CompletableFuture.completedFuture(null);
    }
}
