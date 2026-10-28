import java.io.InputStream;
import java.util.function.Supplier;
import minecraft.portable.identity.PortableLanAutoPublishTransformer;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.FieldInsnNode;
import org.objectweb.asm.tree.InsnList;
import org.objectweb.asm.tree.InsnNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;
import org.objectweb.asm.tree.VarInsnNode;

/** Exercises the native button builder and lazy screen-opening lifecycle after bytecode rewriting. */
public final class PauseReportingProbe {
    private static final String TARGET = "PauseReportingProbe$PauseScreen";
    private static final String DESCRIPTOR = "(LPauseReportingProbe$Component;Ljava/util/function/Supplier;)LPauseReportingProbe$Button;";
    private static Class<?> patched;

    public static void main(String[] args) throws Exception {
        configure();
        byte[] original;
        try (InputStream stream = PauseReportingProbe.class.getResourceAsStream("/" + TARGET + ".class")) {
            original = stream.readAllBytes();
        }
        property("lanPublishEnabled", "false");
        check(transform(original) == null, "disabled LAN changed pause menu");
        property("lanPublishEnabled", "true");
        property("pauseReportingEnabled", "false");
        check(transform(original) == null, "disabled reporting changed pause menu");
        property("pauseReportingEnabled", "true");
        check(new PortableLanAutoPublishTransformer().transform(null, null, "other/Menu", null, null, original) == null,
            "unrelated menu changed");
        byte[] transformed = transform(original);
        check(transformed != null, "native menu not patched");
        verifySites(transformed);
        rejectShape(original, "static");
        rejectShape(original, "special");
        rejectShape(original, "descriptor");
        rejectShape(original, "duplicate");
        patched = new Loader().define(transformed);
        checkMenu(new Server(false), false);
        Server published = new Server(true);
        checkMenu(published, true);
        check(published.publishCalls == 0, "reporting path published another session");
        checkMenu(null, false); // No integrated server, including a remote multiplayer connection.
        checkMenu(new Object(), false); // Unknown server API conservatively keeps the native options.
        Server unavailable = new Server(true);
        unavailable.throwState = true;
        checkMenu(unavailable, false);
        System.out.println("Native pause reporting checks passed.");
    }

    private static void checkMenu(Object server, boolean reporting) throws Exception {
        Minecraft.INSTANCE = new Minecraft(server);
        MenuBase menu = (MenuBase) patched.getConstructor().newInstance();
        menu.createPauseMenu();
        check(menu.nativeInits == 1 && menu.builderCalls == 2 && menu.nativeTail == 1,
            "native menu initialization or button builder was replaced");
        Component expected = (Component) patched.getField(reporting ? "PLAYER_REPORTING" : "MULTIPLAYER_OPTIONS").get(null);
        check(menu.multiplayer.label == expected, "button lost exact native caption");
        check("native.other".equals(menu.other.label.key), "unrelated native button changed");
        check(Minecraft.INSTANCE.constructed == 0 && Minecraft.INSTANCE.opened == 0,
            "screen was constructed or opened before the native button click");
        menu.multiplayer.click();
        Screen opened = Minecraft.INSTANCE.current;
        check(reporting ? opened instanceof ReportingScreen : opened instanceof OptionsScreen,
            "button opened wrong native screen");
        check(opened.parent == menu && Minecraft.INSTANCE.opened == 1 && Minecraft.INSTANCE.constructed == 1,
            "native screen supplier/lifecycle/parent lost");
        opened.onClose();
        check(Minecraft.INSTANCE.current == menu && Minecraft.INSTANCE.returned == 1,
            "native screen did not return to its pause parent");

        // Resize/rebuild keeps the action derived from publication and performs no publication itself.
        menu.createPauseMenu();
        check(menu.nativeInits == 2 && menu.nativeTail == 2 && menu.multiplayer.label == expected,
            "resize lost native menu or selected a different caption");
        check(Minecraft.INSTANCE.constructed == 1, "resize eagerly constructed screen");
        menu.buildOtherMethod();
        Component nativeOptions = (Component) patched.getField("MULTIPLAYER_OPTIONS").get(null);
        check(menu.unrelatedSite.label == nativeOptions, "matching call in unrelated method was rewritten");
        menu.unrelatedSite.click();
        check(Minecraft.INSTANCE.current instanceof OptionsScreen, "unrelated native action changed");
        menu.other.click();
        check(Minecraft.INSTANCE.current instanceof OtherScreen, "unrelated button supplier changed");
        if (server instanceof Server nativeServer) check(nativeServer.publishCalls == 0,
            "pause rebuild or navigation created a new session");
    }

    private static byte[] transform(byte[] original) {
        return new PortableLanAutoPublishTransformer().transform(null, null, TARGET, null, null, original);
    }

    private static void verifySites(byte[] transformed) {
        ClassNode node = read(transformed);
        int hooks = 0;
        for (MethodNode method : node.methods) {
            for (var instruction : method.instructions) {
                if (instruction instanceof MethodInsnNode call && call.name.equals("pauseButton")) {
                    hooks++;
                    check(method.name.equals("createPauseMenu"), "hook inserted outside native pause builder");
                }
            }
        }
        check(hooks == 1, "more than the native multiplayer slot was intercepted");
    }

    private static void rejectShape(byte[] original, String mode) {
        ClassNode node = read(original);
        for (MethodNode method : node.methods) {
            if (!method.name.equals("createPauseMenu")) continue;
            MethodInsnNode selected = null;
            for (var instruction : method.instructions) {
                if (instruction instanceof MethodInsnNode call && call.owner.equals(TARGET) &&
                    call.name.equals("openScreenButton")) { selected = call; break; }
            }
            check(selected != null, "fixture lost native builder call");
            switch (mode) {
                case "static": selected.setOpcode(Opcodes.INVOKESTATIC); break;
                case "special": selected.setOpcode(Opcodes.INVOKESPECIAL); break;
                case "descriptor": selected.desc = "(Ljava/lang/Object;Ljava/util/function/Supplier;)LPauseReportingProbe$Button;"; break;
                case "duplicate":
                    InsnList duplicate = new InsnList();
                    duplicate.add(new VarInsnNode(Opcodes.ALOAD, 0));
                    duplicate.add(new FieldInsnNode(Opcodes.GETSTATIC, TARGET, "MULTIPLAYER_OPTIONS", "LPauseReportingProbe$Component;"));
                    duplicate.add(new InsnNode(Opcodes.ACONST_NULL));
                    duplicate.add(new MethodInsnNode(Opcodes.INVOKEVIRTUAL, TARGET, "openScreenButton", DESCRIPTOR, false));
                    duplicate.add(new InsnNode(Opcodes.POP));
                    method.instructions.insertBefore(method.instructions.getLast(), duplicate);
                    break;
                default: throw new AssertionError(mode);
            }
        }
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        try {
            transform(writer.toByteArray());
            throw new AssertionError("unsafe pause bytecode accepted: " + mode);
        } catch (IllegalStateException expected) {
            check(expected.getMessage().contains("expected one native multiplayer pause button"),
                "unexpected rejection for " + mode + ": " + expected);
        }
    }

    private static ClassNode read(byte[] bytes) {
        ClassNode node = new ClassNode();
        new ClassReader(bytes).accept(node, 0);
        return node;
    }

    private static void configure() {
        property("pauseScreenClasses", TARGET);
        property("lanShareScreenClasses", "unrelated/ShareScreen");
        property("pauseCreateMenuMethods", "createPauseMenu");
        property("pauseMultiplayerFields", "MULTIPLAYER_OPTIONS");
        property("pausePlayerReportingFields", "PLAYER_REPORTING");
        property("pauseOpenScreenButtonMethods", "openScreenButton");
        property("pauseOpenScreenButtonDescriptors", DESCRIPTOR);
        property("componentClasses", Component.class.getName());
        property("minecraftClasses", Minecraft.class.getName());
        property("minecraftGetInstanceMethods", "getInstance");
        property("getSingleplayerServerMethods", "getSingleplayerServer");
        property("isPublishedMethods", "isPublished");
        property("screenClasses", Screen.class.getName());
        property("reportingScreenClasses", ReportingScreen.class.getName());
    }

    private static void property(String name, String value) { System.setProperty("minecraft.portable.identity." + name, value); }
    private static void check(boolean condition, String message) { if (!condition) throw new AssertionError(message); }

    public static class Screen {
        public Screen parent;
        public Screen(Screen parent) { this.parent = parent; }
        public void onClose() { Minecraft.INSTANCE.current = parent; Minecraft.INSTANCE.returned++; }
    }
    public static class MenuBase extends Screen {
        public Button multiplayer;
        public Button other;
        public Button unrelatedSite;
        public int nativeInits;
        public int nativeTail;
        public int builderCalls;
        public MenuBase() { super(null); }
        public void createPauseMenu() { throw new AssertionError("base menu called"); }
        public void buildOtherMethod() { throw new AssertionError("base other menu called"); }
    }
    public static final class PauseScreen extends MenuBase {
        public static final Component MULTIPLAYER_OPTIONS = new Component("native.multiplayerOptions");
        public static final Component PLAYER_REPORTING = new Component("native.playerReporting");
        public static final Component OTHER = new Component("native.other");
        @Override public void createPauseMenu() {
            nativeInits++;
            multiplayer = openScreenButton(MULTIPLAYER_OPTIONS, () -> new OptionsScreen(this));
            other = openScreenButton(OTHER, () -> new OtherScreen(this));
            nativeTail++;
        }
        @Override public void buildOtherMethod() {
            unrelatedSite = openScreenButton(MULTIPLAYER_OPTIONS, () -> new OptionsScreen(this));
        }
        public Button openScreenButton(Component label, Supplier<? extends Screen> action) {
            builderCalls++;
            return new Button(label, () -> { Minecraft.INSTANCE.current = action.get(); Minecraft.INSTANCE.opened++; });
        }
    }
    public static final class Button {
        public final Component label;
        public final Runnable action;
        public Button(Component label, Runnable action) { this.label = label; this.action = action; }
        public void click() { action.run(); }
    }
    public static final class Component {
        public final String key;
        public Component(String key) { this.key = key; }
    }
    public static final class OptionsScreen extends Screen {
        public OptionsScreen(Screen parent) { super(parent); Minecraft.INSTANCE.constructed++; }
    }
    public static final class ReportingScreen extends Screen {
        public ReportingScreen(Screen parent) { super(parent); Minecraft.INSTANCE.constructed++; }
    }
    public static final class OtherScreen extends Screen {
        public OtherScreen(Screen parent) { super(parent); Minecraft.INSTANCE.constructed++; }
    }
    public static final class Server {
        public boolean published;
        public boolean throwState;
        public int publishCalls;
        public Server(boolean published) { this.published = published; }
        public boolean isPublished() {
            if (throwState) throw new IllegalStateException("unknown fixture publication state");
            return published;
        }
        public void publishServer() { publishCalls++; }
    }
    public static final class Minecraft {
        public static Minecraft INSTANCE;
        public Object server;
        public Screen current;
        public int constructed;
        public int opened;
        public int returned;
        public Minecraft(Object server) { this.server = server; }
        public static Minecraft getInstance() { return INSTANCE; }
        public Object getSingleplayerServer() { return server; }
    }
    private static final class Loader extends ClassLoader {
        Class<?> define(byte[] bytes) { return defineClass(TARGET, bytes, 0, bytes.length); }
    }
}
