/** Vanilla 26.3's scoped native method and the delegating overload called by mods. */
public final class SteamDiscoveryFakePublisher extends SteamDiscoveryBehaviorProbe.Server {
    public enum Scope { OFF, LAN }

    private final SteamDiscoveryBehaviorProbe.Minecraft minecraft;
    private boolean published;

    public SteamDiscoveryFakePublisher(SteamDiscoveryBehaviorProbe.Minecraft minecraft) {
        this.minecraft = minecraft;
    }

    public boolean publishServer(Scope scope, boolean commands, int port) {
        return publishServer(scope, port);
    }

    public boolean publishServer(Scope scope, int port) {
        if (scope == Scope.OFF || published || port == -1) return false;
        if (port == -2) throw new IllegalArgumentException("native exception");
        published = true;
        return true;
    }
}
