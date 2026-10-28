import java.io.InputStream;
import java.util.ArrayDeque;
import java.util.Queue;
import java.util.concurrent.Executor;
import minecraft.portable.identity.PortableLanAutoPublishHooks;
import minecraft.portable.identity.PortableLanAutoPublishTransformer;

/** Executes the transformed init and the real common hook, with native API shapes from both eras. */
public final class LanAutoPublishProbe {
    private static final String TARGET = "LanAutoPublishProbe$ShareScreen";
    private static boolean modern;
    private static Class<?> patchedScreen;

    public static void main(String[] args) throws Exception {
        modern = args[0].equals("modern");
        configure();
        byte[] original;
        try (InputStream stream = LanAutoPublishProbe.class.getResourceAsStream("/" + TARGET + ".class")) {
            original = stream.readAllBytes();
        }
        PortableLanAutoPublishTransformer transformer = new PortableLanAutoPublishTransformer();
        property("lanPublishEnabled", "false");
        check(transformer.transform(null, null, TARGET, null, null, original) == null, "disabled hook patched screen");
        property("lanPublishEnabled", "true");
        check(transformer.transform(null, null, "other/Screen", null, null, original) == null,
            "unrelated class patched");
        byte[] transformed = transformer.transform(null, null, TARGET, null, null, original);
        check(transformed != null, "screen was not transformed");
        patchedScreen = new Loader().define(transformed);

        successfulPublish(false);
        successfulPublish(true);
        failedPublish(false);
        failedPublish(true);
        noIntegratedServer();
        check(!PortableLanAutoPublishHooks.autoPublish(new Component(0)), "non-screen triggered publication");
        System.out.println("LAN lifecycle and publication checks passed: " + args[0] + ".");
    }

    private static void successfulPublish(boolean earlyReturn) throws Exception {
        Minecraft client = reset();
        Server server = client.server;
        BaseScreen screen = screen();
        screen.earlyReturn = earlyReturn;
        screen.init(7);
        check(screen.overloadCalls == 1 && server.attempts == 0, "non-native init overload patched");
        screen.init();
        check(screen.nativeInitializations == 1 && screen.widgetsReady, "native init was skipped");
        check(screen.nativeTailRuns == (earlyReturn ? 0 : 1), "native return path changed");
        check(server.attempts == 1 && server.published, "world did not publish once");
        check(client.gui.current == screen && client.closes == 0, "screen closed before init lifecycle completed");
        check(client.queue.size() == 1, "screen close was not deferred exactly once");
        check(client.titles == 1 && client.gui.chat.messages == 1, "normal publication feedback lost");
        check(server.guestMode == GameType.CREATIVE && !server.guestCommands,
            "publication altered guest settings");
        if (modern) check(server.scope == Scope.LAN && server.modernCalls == 1 && server.legacyCalls == 0,
            "modern native scope/overload incorrect");
        else check(server.legacyCalls == 1 && server.modernCalls == 0 &&
            server.usedMode == GameType.SURVIVAL && server.usedCommands,
            "legacy world defaults/overload incorrect");

        // Resize re-runs the same native init; it must still finish, without another session or close.
        screen.init();
        check(screen.nativeInitializations == 2 && server.attempts == 1 && client.queue.size() == 1,
            "resize created another publication");
        client.drain();
        check(client.closes == 1 && client.gui.current == null, "screen was not dismissed");

        // A menu that permits the button again may construct a fresh screen for an already-open world.
        BaseScreen again = screen();
        again.init();
        check(again.widgetsReady && server.attempts == 1, "second screen republished world");
        client.drain();
        check(client.closes == 2, "second screen was left on an already-open world");
    }

    private static void failedPublish(boolean throwing) throws Exception {
        Minecraft client = reset();
        client.server.succeed = false;
        client.server.throwOnPublish = throwing;
        BaseScreen screen = screen();
        screen.init();
        check(screen.widgetsReady && client.server.attempts == 1, "failure did not preserve native screen");
        check(client.gui.current == screen && client.queue.isEmpty() && client.closes == 0,
            "failed publication hid settings");
        check(client.titles == 0 && client.gui.chat.messages == 0, "failure reported success");
        screen.init();
        check(screen.nativeInitializations == 2 && client.server.attempts == 1,
            "resize retried failed publication");
        BaseScreen retry = screen();
        retry.init();
        check(client.server.attempts == 2, "new intentional open could not retry publication");
    }

    private static void noIntegratedServer() throws Exception {
        Minecraft client = reset();
        client.server = null;
        BaseScreen screen = screen();
        screen.init();
        check(screen.widgetsReady && client.gui.current == screen && client.queue.isEmpty(),
            "non-integrated session screen was changed");
    }

    private static Minecraft reset() {
        Minecraft.INSTANCE = new Minecraft();
        return Minecraft.INSTANCE;
    }

    private static BaseScreen screen() throws Exception {
        BaseScreen screen = (BaseScreen) patchedScreen.getConstructor().newInstance();
        Minecraft.INSTANCE.gui.current = screen;
        return screen;
    }

    private static void configure() {
        property("lanShareScreenClasses", TARGET);
        property("lanShareInitMethods", "init");
        property("minecraftClasses", Minecraft.class.getName());
        property("screenClasses", BaseScreen.class.getName());
        property("gameTypeClasses", GameType.class.getName());
        property("httpUtilClasses", HttpUtil.class.getName());
        property("publishCommandClasses", PublishCommand.class.getName());
        property("componentClasses", Component.class.getName());
        property("minecraftGetInstanceMethods", "getInstance");
        property("getSingleplayerServerMethods", "getSingleplayerServer");
        property("isPublishedMethods", "isPublished");
        property("getDefaultGameTypeMethods", "getDefaultGameType");
        property("getWorldDataMethods", "getWorldData");
        property("isAllowCommandsMethods", "isAllowCommands");
        property("getAvailablePortMethods", "getAvailablePort");
        property("publishServerMethods", "publishServer");
        property("minecraftGuiFields", "gui");
        property("guiChatMethods", "getChat");
        property("chatAddMessageMethods", "addMessage");
        property("publishSuccessMethods", "getSuccessMessage");
        property("updateTitleMethods", "updateTitle");
        property("setScreenMethods", "setScreen");
        if (modern) {
            property("multiplayerScopeClasses", Scope.class.getName());
            property("lanMultiplayerScopeFields", "LAN");
            property("setScreenOnGui", "true");
            property("minecraftScheduleMethods", "schedule");
            property("guiHudFields", "hud");
            property("chatAddMessageMethods", "addClientSystemMessage");
        }
    }

    private static void property(String name, String value) {
        System.setProperty("minecraft.portable.identity." + name, value);
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }

    public static class BaseScreen {
        public int nativeInitializations;
        public int overloadCalls;
        public boolean widgetsReady;
        public boolean earlyReturn;
        public int nativeTailRuns;
        public void init() { throw new AssertionError("untransformed base init"); }
        public void init(int unused) { overloadCalls++; }
    }

    public static final class ShareScreen extends BaseScreen {
        @Override public void init() {
            nativeInitializations++;
            widgetsReady = true;
            if (earlyReturn) return;
            nativeTailRuns++;
        }
    }

    public enum Scope { SINGLEPLAYER, LAN, ONLINE }
    public enum GameType { SURVIVAL, CREATIVE }
    public static final class WorldData { public boolean isAllowCommands() { return true; } }

    public static final class Server {
        public boolean published;
        public boolean succeed = true;
        public boolean throwOnPublish;
        public int attempts;
        public int modernCalls;
        public int legacyCalls;
        public GameType guestMode = GameType.CREATIVE;
        public boolean guestCommands;
        public GameType usedMode;
        public boolean usedCommands;
        public Scope scope;
        public boolean isPublished() { return published; }
        public GameType getDefaultGameType() { return GameType.SURVIVAL; }
        public WorldData getWorldData() { return new WorldData(); }
        public boolean publishServer(Scope requested, int port) {
            check(modern, "modern overload called for legacy runtime");
            modernCalls++;
            scope = requested;
            return publish(port);
        }
        public boolean publishServer(GameType mode, boolean commands, int port) {
            check(!modern, "legacy overload called for modern runtime");
            legacyCalls++;
            usedMode = mode;
            usedCommands = commands;
            return publish(port);
        }
        public boolean publishServer(Scope requested, GameType mode, boolean commands, int port) {
            throw new AssertionError("settings-changing four-argument overload called");
        }
        private boolean publish(int port) {
            check(Minecraft.INSTANCE.gui.current.widgetsReady, "publish ran before native init");
            check(port == 35658, "native available port was not used");
            attempts++;
            if (throwOnPublish) throw new IllegalStateException("intentional fixture publish failure");
            published = succeed;
            return succeed;
        }
    }

    public static final class Minecraft implements Executor {
        public static Minecraft INSTANCE;
        public Server server = new Server();
        public Gui gui = new Gui();
        public Queue<Runnable> queue = new ArrayDeque<>();
        public int closes;
        public int titles;
        public static Minecraft getInstance() { return INSTANCE; }
        public Server getSingleplayerServer() { return server; }
        public void updateTitle() { titles++; }
        public void setScreen(BaseScreen screen) {
            check(!modern, "modern client attempted obsolete Minecraft.setScreen");
            gui.current = screen;
            closes++;
        }
        @Override public void execute(Runnable task) {
            // 26.2's executor can run on the render thread immediately; schedule must be used there.
            if (modern) task.run();
            else queue.add(task);
        }
        public void schedule(Runnable task) { queue.add(task); }
        public void drain() { while (!queue.isEmpty()) queue.remove().run(); }
    }

    public static final class Gui {
        public BaseScreen current;
        public Chat chat = new Chat();
        public Hud hud = new Hud(chat);
        public Chat getChat() {
            check(!modern, "modern chat bypassed Gui.hud");
            return chat;
        }
        public void setScreen(BaseScreen screen) {
            check(modern, "legacy client attempted modern Gui.setScreen");
            current = screen;
            Minecraft.INSTANCE.closes++;
        }
    }

    public static final class Chat {
        public int messages;
        public void addMessage(Component message) {
            check(!modern, "modern chat used obsolete addMessage");
            record(message);
        }
        public void addClientSystemMessage(Component message) {
            check(modern, "legacy chat used modern entry point");
            record(message);
        }
        private void record(Component message) { check(message.port == 35658, "wrong chat port"); messages++; }
    }
    public static final class Hud {
        private final Chat chat;
        public Hud(Chat chat) { this.chat = chat; }
        public Chat getChat() { return chat; }
    }
    public static final class Component {
        public final int port;
        public Component(int port) { this.port = port; }
    }
    public static final class PublishCommand {
        public static Component getSuccessMessage(int port) { return new Component(port); }
    }
    public static final class HttpUtil { public static int getAvailablePort() { return 35658; } }

    private static final class Loader extends ClassLoader {
        Class<?> define(byte[] bytes) { return defineClass(TARGET, bytes, 0, bytes.length); }
    }
}
