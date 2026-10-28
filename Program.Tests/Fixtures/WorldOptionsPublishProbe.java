import java.io.InputStream;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.Queue;
import java.util.concurrent.Executor;
import java.util.function.Supplier;
import minecraft.portable.identity.PortableLanAutoPublishTransformer;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.InsnList;
import org.objectweb.asm.tree.InsnNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;

/** Keeps ordinary world settings intact and publishes only an explicitly requested native screen. */
public final class WorldOptionsPublishProbe {
    private static final String PAUSE = "WorldOptionsPublishProbe$PauseScreen";
    private static final String WORLD = "WorldOptionsPublishProbe$WorldOptionsScreen";
    private static final String ROW = "WorldOptionsPublishProbe$RowHelper";
    private static Class<?> pauseType;
    private static Class<?> worldType;

    public static void main(String[] args) throws Exception {
        configure();
        Loader loader = new Loader();
        byte[] pause = bytes(PAUSE);
        byte[] world = bytes(WORLD);
        PortableLanAutoPublishTransformer transformer = new PortableLanAutoPublishTransformer();
        property("pauseLanRowEnabled", "false");
        check(transformer.transform(null, null, PAUSE, null, null, pause) == null, "disabled row feature changed pause");
        property("pauseLanRowEnabled", "true");
        rejectRow(pause, "opcode");
        rejectRow(pause, "descriptor");
        rejectRow(pause, "duplicate");
        pauseType = loader.define(PAUSE, transformer.transform(null, null, PAUSE, null, null, pause));
        worldType = loader.define(WORLD, transformer.transform(null, null, WORLD, null, null, world));
        successfulPublication();
        failedPublication(false);
        failedPublication(true);
        noIntegratedRow(null);
        noIntegratedRow(new Object());
        Server unknown = new Server();
        unknown.throwState = true;
        noIntegratedRow(unknown);
        System.out.println("Explicit world options publication checks passed.");
    }

    private static void successfulPublication() throws Exception {
        Minecraft client = reset(new Server());
        Server server = (Server) client.server;
        MenuBase pause = menu();
        checkRow(pause, false);
        check(server.attempts == 0, "building a pause row published a session");
        pause.otherLayout();
        check(pause.unrelatedRows.children.size() == 1, "unrelated layout method gained a LAN row");
        pause.createPauseMenu();
        checkRow(pause, false);
        check(pause.nativeInits == 2 && server.attempts == 0, "pause resize altered native initialization or published");

        // The ordinary World Options action remains available and must not open the world or disappear.
        pause.options.click();
        WorldBase ordinary = (WorldBase) client.gui.current;
        check(ordinary.widgetsReady && ordinary.nativeInits == 1 && ordinary.parent == pause,
            "ordinary world settings lost native lifecycle/parent");
        check(server.attempts == 0 && client.queue.isEmpty(), "ordinary world options auto-published or closed");
        ordinary.init();
        check(server.attempts == 0 && client.gui.current == ordinary, "ordinary settings resize auto-published");

        client.gui.current = pause;
        ((Button) pause.rows.children.get(1)).click();
        WorldBase requested = (WorldBase) client.gui.current;
        check(requested != ordinary && requested.widgetsReady && requested.parent == pause &&
            requested.level == client.level, "explicit action lost native world options constructor/state");
        check(server.published && server.attempts == 1 && server.scope == Scope.LAN,
            "explicit native screen did not publish exactly once");
        check(client.gui.current == requested && client.queue.size() == 1,
            "native init was closed synchronously or not scheduled");
        requested.init();
        check(requested.nativeInits == 2 && server.attempts == 1 && client.queue.size() == 1,
            "requested screen resize repeated publication");
        client.drain();
        check(client.gui.current == null && client.closes == 1, "successful publication left settings visible");

        pause.createPauseMenu();
        checkRow(pause, true);
        check(client.constructedReports == 0, "report screen was not lazy");
        ((Button) pause.rows.children.get(1)).click();
        check(client.gui.current instanceof ReportingScreen && client.gui.current.parent == pause &&
            client.constructedReports == 1 && server.attempts == 1, "published row did not use native reports/parent");
        client.gui.current.onClose();
        check(client.gui.current == pause, "reporting did not return to native pause screen");

        // Ordinary settings still behave as ordinary settings after publication as well.
        pause.options.click();
        WorldBase afterPublish = (WorldBase) client.gui.current;
        check(afterPublish.widgetsReady && server.attempts == 1 && client.queue.isEmpty(),
            "published world's ordinary settings were treated as Open LAN");
    }

    private static void failedPublication(boolean throwing) throws Exception {
        Server server = new Server();
        server.succeed = false;
        server.throwPublish = throwing;
        Minecraft client = reset(server);
        MenuBase pause = menu();
        ((Button) pause.rows.children.get(1)).click();
        WorldBase requested = (WorldBase) client.gui.current;
        check(requested.widgetsReady && requested.nativeInits == 1 && server.attempts == 1,
            "failed publication skipped native world settings");
        check(client.queue.isEmpty() && client.gui.current == requested && client.closes == 0,
            "failed publication hid native settings");
        requested.init();
        check(server.attempts == 1 && requested.nativeInits == 2, "failed screen resize retried publication");
        pause.createPauseMenu();
        checkRow(pause, false);
        ((Button) pause.rows.children.get(1)).click();
        check(server.attempts == 2, "fresh explicit action could not retry failure");
    }

    private static void noIntegratedRow(Object server) throws Exception {
        Minecraft client = reset(server);
        MenuBase pause = menu();
        check(pause.rows.children.size() == 2 && pause.rows.children.get(0) == pause.options &&
            pause.rows.children.get(1) == pause.exitButton, "unknown/remote server gained a LAN row");
        check(pause.nativeTail == 1 && pause.exitButton.width == 345, "native exit layout changed");
        pause.options.click();
        check(client.gui.current instanceof WorldBase && client.queue.isEmpty(),
            "ordinary remote/unknown world options were changed");
    }

    private static void checkRow(MenuBase menu, boolean published) throws Exception {
        check(menu.rows.children.size() == 3 && menu.rows.children.get(0) == menu.options &&
            menu.rows.children.get(2) == menu.exitButton, "new row was not inserted immediately before native exit");
        Button action = (Button) menu.rows.children.get(1);
        check(action.width == menu.exitButton.width && action.width == 345,
            "full-width native LAN/report row did not copy actual exit width");
        check(menu.rows.columns.get(1) == 2 && menu.rows.columns.get(2) == 2,
            "injected row did not retain native full-width column span");
        if (published) check(action.label == pauseType.getField("PLAYER_REPORTING").get(null),
            "report row did not keep exact native component");
        else check("menu.multiplayerOptions.button".equals(action.label.key), "Open LAN did not use native translation key");
        check(menu.options.label.key.equals("native.worldOptions") && menu.exitButton.label.key.equals("native.exit"),
            "existing native actions were renamed");
    }

    private static Minecraft reset(Object server) {
        Minecraft.INSTANCE = new Minecraft(server);
        return Minecraft.INSTANCE;
    }
    private static MenuBase menu() throws Exception {
        MenuBase pause = (MenuBase) pauseType.getConstructor().newInstance();
        pause.createPauseMenu();
        Minecraft.INSTANCE.gui.current = pause;
        return pause;
    }
    public static Screen ordinaryWorldOptions(Screen parent, Level level) {
        try { return (Screen) worldType.getConstructor(Screen.class, Level.class).newInstance(parent, level); }
        catch (ReflectiveOperationException error) { throw new AssertionError(error); }
    }
    private static byte[] bytes(String target) throws Exception {
        try (InputStream stream = WorldOptionsPublishProbe.class.getResourceAsStream("/" + target + ".class")) {
            return stream.readAllBytes();
        }
    }
    private static void rejectRow(byte[] original, String mutation) {
        ClassNode node = new ClassNode();
        new ClassReader(original).accept(node, 0);
        for (MethodNode method : node.methods) {
            if (!method.name.equals("createPauseMenu")) continue;
            for (var instruction : method.instructions.toArray()) {
                if (!(instruction instanceof MethodInsnNode call) || !call.owner.equals(ROW) ||
                    !call.desc.equals("(LWorldOptionsPublishProbe$LayoutElement;I)LWorldOptionsPublishProbe$LayoutElement;")) continue;
                if (mutation.equals("opcode")) call.setOpcode(Opcodes.INVOKESPECIAL);
                else if (mutation.equals("descriptor")) call.desc = "(Ljava/lang/Object;I)LWorldOptionsPublishProbe$LayoutElement;";
                else {
                    InsnList duplicate = new InsnList();
                    duplicate.add(new InsnNode(Opcodes.ACONST_NULL));
                    duplicate.add(new InsnNode(Opcodes.ACONST_NULL));
                    duplicate.add(new InsnNode(Opcodes.ICONST_2));
                    duplicate.add(new MethodInsnNode(Opcodes.INVOKEVIRTUAL, ROW, "addChild", call.desc, false));
                    duplicate.add(new InsnNode(Opcodes.POP));
                    method.instructions.insertBefore(call, duplicate);
                }
                break;
            }
        }
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        try {
            new PortableLanAutoPublishTransformer().transform(null, null, PAUSE, null, null, writer.toByteArray());
            throw new AssertionError("unsafe native row shape accepted: " + mutation);
        } catch (IllegalStateException expected) {
            check(expected.getMessage().contains("disconnect row"), "unexpected row rejection: " + expected);
        }
    }
    private static void configure() {
        property("lanPublishEnabled", "true");
        property("lanPublishExplicitRequestOnly", "true");
        property("lanShareScreenClasses", WORLD);
        property("lanShareInitMethods", "init");
        property("pauseReportingEnabled", "false");
        property("pauseScreenClasses", PAUSE);
        property("pauseCreateMenuMethods", "createPauseMenu");
        property("pausePlayerReportingFields", "PLAYER_REPORTING");
        property("pauseOpenScreenButtonMethods", "openScreenButton");
        property("pauseOpenScreenButtonDescriptors", "(LWorldOptionsPublishProbe$Component;Ljava/util/function/Supplier;)LWorldOptionsPublishProbe$Button;");
        property("pauseRowHelperClasses", ROW);
        property("pauseRowAddMethods", "addChild");
        property("pauseRowAddDescriptors", "(LWorldOptionsPublishProbe$LayoutElement;I)LWorldOptionsPublishProbe$LayoutElement;");
        property("layoutElementClasses", LayoutElement.class.getName());
        property("widgetGetWidthMethods", "getWidth");
        property("widgetSetWidthMethods", "setWidth");
        property("worldOptionsScreenClasses", WORLD);
        property("levelClasses", Level.class.getName());
        property("minecraftLevelFields", "level");
        property("componentTranslatableMethods", "translatable");
        property("reportingScreenClasses", ReportingScreen.class.getName());
        property("minecraftClasses", Minecraft.class.getName());
        property("minecraftGetInstanceMethods", "getInstance");
        property("getSingleplayerServerMethods", "getSingleplayerServer");
        property("isPublishedMethods", "isPublished");
        property("screenClasses", Screen.class.getName());
        property("componentClasses", Component.class.getName());
        property("gameTypeClasses", GameType.class.getName());
        property("httpUtilClasses", HttpUtil.class.getName());
        property("publishCommandClasses", PublishCommand.class.getName());
        property("multiplayerScopeClasses", Scope.class.getName());
        property("lanMultiplayerScopeFields", "LAN");
        property("getAvailablePortMethods", "getAvailablePort");
        property("publishServerMethods", "publishServer");
        property("setScreenOnGui", "true");
        property("minecraftGuiFields", "gui");
        property("setScreenMethods", "setScreen");
        property("minecraftScheduleMethods", "schedule");
        property("updateTitleMethods", "updateTitle");
    }
    private static void property(String key, String value) { System.setProperty("minecraft.portable.identity." + key, value); }
    private static void check(boolean condition, String message) { if (!condition) throw new AssertionError(message); }

    public interface LayoutElement { }
    public static class Screen {
        public final Screen parent;
        public Screen(Screen parent) { this.parent = parent; }
        public void init() { }
        public void onClose() { Minecraft.INSTANCE.gui.current = parent; }
    }
    public static class MenuBase extends Screen {
        public RowHelper rows;
        public RowHelper unrelatedRows;
        public Button options;
        public Button exitButton;
        public int nativeInits;
        public int nativeTail;
        public MenuBase() { super(null); }
        public void createPauseMenu() { throw new AssertionError("base menu"); }
        public void otherLayout() { throw new AssertionError("base unrelated menu"); }
    }
    public static final class PauseScreen extends MenuBase {
        public static final Component PLAYER_REPORTING = new Component("native.playerReporting");
        public void createPauseMenu() {
            nativeInits++;
            rows = new RowHelper();
            options = openScreenButton(new Component("native.worldOptions"),
                () -> ordinaryWorldOptions(this, Minecraft.INSTANCE.level));
            rows.addChild(options);
            Button nativeExit = new Button(new Component("native.exit"), () -> { });
            nativeExit.setWidth(345);
            exitButton = (Button) rows.addChild(nativeExit, 2);
            nativeTail++;
        }
        public Button openScreenButton(Component component, Supplier<? extends Screen> action) {
            return new Button(component, () -> Minecraft.INSTANCE.gui.setScreen(action.get()));
        }
        @Override public void otherLayout() {
            unrelatedRows = new RowHelper();
            unrelatedRows.addChild(new Button(new Component("native.unrelated"), () -> { }), 2);
        }
    }
    public static final class RowHelper {
        public final List<LayoutElement> children = new ArrayList<>();
        public final List<Integer> columns = new ArrayList<>();
        public LayoutElement addChild(LayoutElement child) { children.add(child); columns.add(1); return child; }
        public LayoutElement addChild(LayoutElement child, int span) { children.add(child); columns.add(span); return child; }
    }
    public static final class Button implements LayoutElement {
        public final Component label;
        public final Runnable action;
        public int width = 98;
        public Button(Component label, Runnable action) { this.label = label; this.action = action; }
        public int getWidth() { return width; }
        public void setWidth(int width) { this.width = width; }
        public void click() { action.run(); }
    }
    public static class WorldBase extends Screen {
        public final Level level;
        public int nativeInits;
        public boolean widgetsReady;
        public WorldBase(Screen parent, Level level) { super(parent); this.level = level; }
    }
    public static final class WorldOptionsScreen extends WorldBase {
        public WorldOptionsScreen(Screen parent, Level level) { super(parent, level); }
        @Override public void init() { nativeInits++; widgetsReady = true; }
    }
    public static final class ReportingScreen extends Screen {
        public ReportingScreen(Screen parent) { super(parent); Minecraft.INSTANCE.constructedReports++; }
    }
    public static final class Component {
        public final String key;
        public Component(String key) { this.key = key; }
        public static Component translatable(String key) { return new Component(key); }
    }
    public static final class Level { }
    public enum Scope { LAN }
    public enum GameType { SURVIVAL }
    public static final class Server {
        public boolean published;
        public boolean succeed = true;
        public boolean throwPublish;
        public boolean throwState;
        public int attempts;
        public Scope scope;
        public boolean isPublished() {
            if (throwState) throw new IllegalStateException("unknown native publication state");
            return published;
        }
        public boolean publishServer(Scope scope, int port) {
            check(Minecraft.INSTANCE.gui.current instanceof WorldBase &&
                ((WorldBase) Minecraft.INSTANCE.gui.current).widgetsReady, "publish ran before native init");
            check(port == 35658, "native available port not used");
            this.scope = scope;
            attempts++;
            if (throwPublish) throw new IllegalStateException("fixture failed publication");
            published = succeed;
            return succeed;
        }
    }
    public static final class Gui {
        public Screen current;
        public void setScreen(Screen screen) {
            current = screen;
            if (screen == null) Minecraft.INSTANCE.closes++;
            else screen.init();
        }
    }
    public static final class Minecraft implements Executor {
        public static Minecraft INSTANCE;
        public final Level level = new Level();
        public final Gui gui = new Gui();
        public final Queue<Runnable> queue = new ArrayDeque<>();
        public Object server;
        public int constructedReports;
        public int closes;
        public Minecraft(Object server) { this.server = server; }
        public static Minecraft getInstance() { return INSTANCE; }
        public Object getSingleplayerServer() { return server; }
        public void schedule(Runnable action) { queue.add(action); }
        public void updateTitle() { }
        @Override public void execute(Runnable action) { action.run(); }
        public void drain() { while (!queue.isEmpty()) queue.remove().run(); }
    }
    public static final class HttpUtil { public static int getAvailablePort() { return 35658; } }
    public static final class PublishCommand { }
    private static final class Loader extends ClassLoader {
        Class<?> define(String name, byte[] bytes) { return defineClass(name, bytes, 0, bytes.length); }
    }
}
