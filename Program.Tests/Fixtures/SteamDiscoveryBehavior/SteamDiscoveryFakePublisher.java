/** Both 26.x native methods and the scoped overloads that mods can call. */
public final class SteamDiscoveryFakePublisher extends SteamDiscoveryBehaviorProbe.Server {
    public enum Scope { OFF, LAN }
    public enum GameType { SURVIVAL }

    private final SteamDiscoveryBehaviorProbe.Minecraft minecraft;
    private boolean published;

    public SteamDiscoveryFakePublisher(SteamDiscoveryBehaviorProbe.Minecraft minecraft) {
        this.minecraft = minecraft;
    }

    public boolean publishServer(Scope scope, boolean commands, int port) {
        return publishServer(scope, port);
    }

    public boolean publishServer(Scope scope, GameType gameType, boolean commands, int port) {
        return publishServer(scope, port);
    }

    public boolean publishServer(GameType gameType, boolean commands, int port) {
        return open(port);
    }

    public boolean publishServer(Scope scope, int port) {
        return scope == Scope.LAN && open(port);
    }

    private boolean open(int port) {
        if (published || port == -1) return false;
        if (port == -2) throw new IllegalArgumentException("native exception");
        published = true;
        return true;
    }
}
