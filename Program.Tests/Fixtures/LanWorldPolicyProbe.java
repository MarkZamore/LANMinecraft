import java.io.InputStream;
import java.lang.reflect.Method;
import minecraft.portable.identity.PortableLanWorldPolicyTransformer;

/** Exercises both transformed server entry points with the native permission precedence. */
public final class LanWorldPolicyProbe {
    private static final String PREFIX = "minecraft.portable.identity.";
    private static final String SERVER = "LanWorldPolicyProbe$IntegratedServer";
    private static final String PLAYERS = "LanWorldPolicyProbe$PlayerList";

    public static void main(String[] args) throws Exception {
        System.setProperty(PREFIX + "integratedServerClasses", SERVER);
        System.setProperty(PREFIX + "playerListClasses", PLAYERS);
        System.setProperty(PREFIX + "publishServerMethods", "publish");
        System.setProperty(PREFIX + "publishServerDescriptors", "(LLanWorldPolicyProbe$Mode;ZI)Z");
        System.setProperty(PREFIX + "getDefaultGameTypeMethods", "getMode");
        System.setProperty(PREFIX + "getWorldDataMethods", "getWorldData");
        System.setProperty(PREFIX + "isAllowCommandsMethods", "isAllowCommands");
        System.setProperty(PREFIX + "lanPolicyPlayerListServerMethods", "getServer");
        System.setProperty(PREFIX + "lanPolicySetAllowCommandsMethods", "setCommandsForEveryone");

        PortableLanWorldPolicyTransformer transformer = new PortableLanWorldPolicyTransformer();
        byte[] original = read(SERVER);
        System.setProperty(PREFIX + "lanPreserveWorldSettingsEnabled", "false");
        check(transformer.transform(null, null, SERVER, null, null, original) == null,
            "policy changed an opted-out pack");
        System.setProperty(PREFIX + "lanPreserveWorldSettingsEnabled", "true");
        check(transformer.transform(null, null, "unrelated/Server", null, null, original) == null,
            "policy transformed an unrelated server");
        Loader loader = new Loader();
        byte[] playerBytes = read(PLAYERS);
        byte[] guardedPlayers = transformer.transform(null, null, PLAYERS, null, null, playerBytes);
        byte[] guardedServer = transformer.transform(null, null, SERVER, null, null, original);
        Method verify = PortableLanWorldPolicyTransformer.class.getDeclaredMethod("verify",
            byte[].class, byte[].class, String.class);
        verify.setAccessible(true);
        verify.invoke(null, playerBytes, guardedPlayers, PLAYERS);
        verify.invoke(null, original, guardedServer, SERVER);
        Class<?> playersType = loader.define(guardedPlayers);
        Class<?> serverType = loader.define(guardedServer);

        Method publish = serverType.getMethod("publish", Mode.class, boolean.class, int.class);
        Method setCommands = playersType.getMethod("setCommandsForEveryone", boolean.class);
        Method canUseCommands = playersType.getMethod("canUseCommands", boolean.class);

        Object locked = serverType.getConstructor(WorldData.class).newInstance(new WorldData(false));
        Object players = serverType.getField("players").get(locked);
        check((Boolean) publish.invoke(locked, Mode.CREATIVE, true, 12345), "saved-world publish failed");
        check(serverType.getField("publishedMode").get(locked) == Mode.SURVIVAL,
            "LAN publication overrode the saved world mode");
        check(serverType.getField("publishedPort").getInt(locked) == 12345, "LAN publication changed the port");
        check(!(Boolean) canUseCommands.invoke(players, true) && !(Boolean) canUseCommands.invoke(players, false),
            "a host or guest received commands in a world with commands disabled");
        setCommands.invoke(players, true);
        check(!(Boolean) canUseCommands.invoke(players, true) && !(Boolean) canUseCommands.invoke(players, false),
            "post-publication setter enabled commands for a host or guest");

        Object optedIn = serverType.getConstructor(WorldData.class).newInstance(new WorldData(true));
        Object allowedPlayers = serverType.getField("players").get(optedIn);
        check((Boolean) publish.invoke(optedIn, Mode.SURVIVAL, true, 12346), "enabled-world publish failed");
        check((Boolean) canUseCommands.invoke(allowedPlayers, true) && (Boolean) canUseCommands.invoke(allowedPlayers, false),
            "commands deliberately enabled at creation were lost");
        check((Boolean) publish.invoke(optedIn, null, false, 12347), "native no-override publish failed");
        check(serverType.getField("publishedMode").get(optedIn) == null &&
            (Boolean) canUseCommands.invoke(allowedPlayers, true) && !(Boolean) canUseCommands.invoke(allowedPlayers, false),
            "native null mode or explicit request to deny guest commands was lost");

        Object dedicatedPlayers = playersType.getConstructor(Object.class).newInstance(new Object());
        setCommands.invoke(dedicatedPlayers, true);
        check((Boolean) canUseCommands.invoke(dedicatedPlayers, false), "integrated policy changed dedicated play");

        System.setProperty(PREFIX + "lanPolicySetAllowCommandsMethods", "wrong");
        try {
            transformer.transform(null, null, PLAYERS, null, null, read(PLAYERS));
            throw new AssertionError("wrong bytecode shape was accepted");
        } catch (IllegalStateException expected) {
            check(expected.getMessage().contains("missing"), "invalid shape lost its diagnostic");
        }
        System.out.println("Saved LAN world policy checks passed.");
    }

    private static byte[] read(String name) throws Exception {
        try (InputStream input = LanWorldPolicyProbe.class.getResourceAsStream("/" + name + ".class")) {
            return input.readAllBytes();
        }
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }

    public enum Mode { SURVIVAL, CREATIVE }

    public static final class WorldData {
        private final boolean allowCommands;
        public WorldData(boolean allowCommands) { this.allowCommands = allowCommands; }
        public boolean isAllowCommands() { return allowCommands; }
    }

    public static final class IntegratedServer {
        private final WorldData worldData;
        public final PlayerList players = new PlayerList(this);
        public Mode publishedMode;
        public int publishedPort;

        public IntegratedServer(WorldData worldData) { this.worldData = worldData; }
        public WorldData getWorldData() { return worldData; }
        public Mode getMode() { return Mode.SURVIVAL; }

        public boolean publish(Mode mode, boolean commands, int port) {
            publishedMode = mode;
            publishedPort = port;
            players.setCommandsForEveryone(commands);
            return true;
        }
    }

    public static final class PlayerList {
        private final Object server;
        private boolean everyone;
        public PlayerList(Object server) { this.server = server; }
        public Object getServer() { return server; }
        public void setCommandsForEveryone(boolean enabled) { everyone = enabled; }
        public boolean canUseCommands(boolean host) {
            return everyone || host && server instanceof IntegratedServer local && local.getWorldData().isAllowCommands();
        }
    }

    private static final class Loader extends ClassLoader {
        Loader() { super(LanWorldPolicyProbe.class.getClassLoader()); }
        Class<?> define(byte[] bytes) { return defineClass(null, bytes, 0, bytes.length); }
    }
}
