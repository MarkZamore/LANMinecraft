import java.lang.reflect.Field;
import java.nio.file.Path;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Queue;
import java.util.concurrent.Executor;
import java.util.zip.ZipFile;
import minecraft.portable.identity.PortableLanAutoPublishHooks;
import minecraft.portable.identity.PortableLanAutoPublishTransformer;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.*;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Actual author callback bodies and actual Mixin cancellation, in an executable post-Mixin shape. */
public final class LanRedirectCoexistenceProbe {
    private static final String SELF = "LanRedirectCoexistenceProbe";
    private static final String SCREEN = SELF + "$Screen";
    private static final String CLIENT = SELF + "$Minecraft";
    private static final String SERVER = SELF + "$Server";
    private static final String CI = "org/spongepowered/asm/mixin/injection/callback/CallbackInfo";
    private static final String CI_DESC = "(L" + CI + ";)V";
    private static final String HANDLER = "handler$actual$redirect";
    private static final String TAIL = "handler$unrelated$transportTail";
    private static String origin;
    private static String target;
    private static String transportSha;
    public static int nativeBodies, otherCallbacks, authorScreens, authorRedirects, publishes, closes;
    public static boolean failPublication;
    public static Object current;
    public static final Queue<Runnable> scheduled = new ArrayDeque<>();

    public static void main(String[] args) throws Exception {
        CallbackInfo realCallback = new CallbackInfo("fixture", true);
        realCallback.cancel();
        check(realCallback.isCancelled(), "real Mixin cancellation is not active");
        try (ZipFile archive = new ZipFile(Path.of(args[0]).toFile())) {
            for (String kind : new String[] { "ShareToLan", "MultiplayerOptions", "WorldOptions", "ForgeShareToLan" }) {
                boolean forge = kind.startsWith("Forge");
                String authorKind = forge ? "ShareToLan" : kind;
                origin = "link.e4steam.mixin." + authorKind + "ScreenMixin";
                target = SELF + "$Merged" + kind;
                transportSha = forge
                    ? "33421acbea4d904ac3cefa62ac1d4bd5f62f9e9169b81a4d16a052d2330bc6fc"
                    : "b7c388393b7c27d188c17981073a2471c7ac52b95d5cff8b8dc8f19946d67be0";
                String entry = (forge ? "forge/" : "") + origin.replace('.', '/') + ".class";
                ClassNode source = read(archive.getInputStream(archive.getEntry(entry)).readAllBytes());
                configure(authorKind.equals("WorldOptions"));
                byte[] merged = merged(source);
                baseline(merged);
                negativeCases(merged);
                successful(merged, authorKind.equals("WorldOptions"));
                failed(merged, authorKind.equals("WorldOptions"));
                System.out.println("passed actual handler: " + kind);
            }
        }
        System.out.println("Pinned author redirect coexistence checks passed: 4 actual handlers.");
    }

    private static void baseline(byte[] merged) throws Exception {
        reset();
        Screen screen = instantiate(merged);
        screen.init();
        check(authorScreens == 1 && authorRedirects == 1, "actual author handler no longer redirects");
        check(nativeBodies == 0 && otherCallbacks == 0 && publishes == 0,
            "baseline did not demonstrate real HEAD cancellation of native initialization");
    }

    private static void successful(byte[] merged, boolean world) throws Exception {
        reset();
        byte[] patched = transform(merged);
        check(handlerCalls(patched) == 0, "supported handler call was not bypassed");
        check(methodCalls(patched, TAIL) == 1, "unrelated merged callback was removed");
        Screen screen = instantiate(patched);
        if (world) {
            screen.init();
            check(nativeBodies == 1 && otherCallbacks == 1 && publishes == 0 && scheduled.isEmpty(),
                "ordinary world settings were auto-published or lost native callbacks");
            request(screen); // The separate pause-entry fixture exercises the native supplier that marks this registry.
        }
        screen.init();
        check(nativeBodies == (world ? 2 : 1) && otherCallbacks == nativeBodies,
            "native init or unrelated callbacks did not finish before publication");
        check(authorScreens == 0 && authorRedirects == 0 && publishes == 1 && scheduled.size() == 1,
            "patched init redirected or failed to publish exactly once");
        screen.init();
        check(publishes == 1 && scheduled.size() == 1, "resize created another publication");
        while (!scheduled.isEmpty()) scheduled.remove().run();
        check(closes == 1 && current == null, "successful native publication was not dismissed");
    }

    private static void failed(byte[] merged, boolean world) throws Exception {
        reset();
        failPublication = true;
        Screen screen = instantiate(transform(merged));
        if (world) request(screen);
        screen.init();
        check(nativeBodies == 1 && otherCallbacks == 1 && publishes == 1 && scheduled.isEmpty() && current == screen,
            "failed publish did not keep the fully initialized native screen");
        screen.init();
        check(publishes == 1 && authorScreens == 0 && authorRedirects == 0,
            "failure resize retried publication or used the author settings form");
    }

    private static void negativeCases(byte[] merged) {
        property("lanRedirectNativeInitEnabled", "false");
        check(handlerCalls(transform(merged)) == 1, "disabled compatibility bypassed the author callback");
        property("lanRedirectNativeInitEnabled", "true");
        for (String mutation : new String[] { "origin", "annotation", "handlerDescriptor" }) {
            ClassNode node = read(merged);
            MethodNode handler = find(node, HANDLER);
            if (mutation.equals("origin")) handler.invisibleAnnotations = List.of(mergedAnnotation("unrelated.OtherMixin"));
            if (mutation.equals("annotation")) handler.invisibleAnnotations = null;
            if (mutation.equals("handlerDescriptor")) handler.desc = "(Ljava/lang/Object;)V";
            check(handlerCalls(transform(write(node))) == 1, "unknown handler provenance/descriptor was bypassed: " + mutation);
        }
        for (String mutation : new String[] { "body", "opcode", "callDescriptor", "duplicateCall", "extraHandler", "allocation" }) {
            ClassNode node = read(merged);
            MethodNode handler = find(node, HANDLER);
            MethodNode init = find(node, "init");
            MethodInsnNode invoke = null;
            for (var instruction : init.instructions) if (instruction instanceof MethodInsnNode call && call.name.equals(HANDLER)) invoke = call;
            if (mutation.equals("body")) handler.instructions.insert(new InsnNode(Opcodes.NOP));
            if (mutation.equals("opcode")) invoke.setOpcode(Opcodes.INVOKESTATIC);
            if (mutation.equals("callDescriptor")) invoke.desc = "(Ljava/lang/Object;)V";
            if (mutation.equals("duplicateCall")) {
                InsnList extra = new InsnList();
                extra.add(new VarInsnNode(Opcodes.ALOAD, 0)); extra.add(new VarInsnNode(Opcodes.ALOAD, 1));
                extra.add(new MethodInsnNode(Opcodes.INVOKESPECIAL, target, HANDLER, CI_DESC, false));
                init.instructions.insertBefore(invoke, extra);
            }
            if (mutation.equals("extraHandler")) {
                MethodNode extra = cloneMethod(handler); extra.name = "handler$extra$redirect"; node.methods.add(extra);
            }
            if (mutation.equals("allocation")) for (var instruction : handler.instructions)
                if (instruction instanceof TypeInsnNode allocation && allocation.getOpcode() == Opcodes.NEW)
                    allocation.desc = "unrelated/SettingsScreen";
            expectRejected(write(node), mutation);
        }
        property("lanRedirectTransportSha256", "not-a-sha");
        expectRejected(merged, "missing transport pin");
        property("lanRedirectTransportSha256", transportSha);
        property("lanRedirectMixinOrigin", "unrelated.OtherMixin");
        expectRejected(merged, "unknown configured origin");
        property("lanRedirectMixinOrigin", origin);
    }

    private static void expectRejected(byte[] bytes, String mutation) {
        try { transform(bytes); throw new AssertionError("unsafe recognized redirect accepted: " + mutation); }
        catch (IllegalStateException expected) {
            check(expected.getMessage().contains("redirect"), "unexpected rejection: " + expected);
        }
    }

    private static byte[] merged(ClassNode source) {
        ClassNode node = new ClassNode();
        node.version = Opcodes.V17; node.access = Opcodes.ACC_PUBLIC; node.name = target; node.superName = SCREEN;
        node.fields.add(new FieldNode(Opcodes.ACC_PUBLIC, "e4steam$showWorldTab", "Z", null, null));
        MethodNode constructor = new MethodNode(Opcodes.ACC_PUBLIC, "<init>", "()V", null, null);
        constructor.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0));
        constructor.instructions.add(new MethodInsnNode(Opcodes.INVOKESPECIAL, SCREEN, "<init>", "()V", false));
        constructor.instructions.add(new InsnNode(Opcodes.RETURN)); node.methods.add(constructor);
        for (MethodNode method : source.methods) {
            if (!method.name.equals("e4steam$openLanSettings") && !method.name.equals("e4steam$openLanTab")) continue;
            MethodNode actual = cloneMethod(method); actual.name = HANDLER;
            actual.visibleAnnotations = null; actual.invisibleAnnotations = new ArrayList<>(List.of(mergedAnnotation(origin)));
            for (var instruction : actual.instructions.toArray()) {
                if (instruction instanceof FrameNode) actual.instructions.remove(instruction);
                else if (instruction instanceof MethodInsnNode call) { call.owner = alias(call.owner, source.name); call.desc = descriptor(call.desc); }
                else if (instruction instanceof FieldInsnNode field) { field.owner = alias(field.owner, source.name); field.desc = descriptor(field.desc); }
                else if (instruction instanceof TypeInsnNode type) type.desc = alias(type.desc, source.name);
            }
            node.methods.add(actual);
        }
        check(find(node, HANDLER) != null, "pinned author handler is missing");
        MethodNode tail = new MethodNode(Opcodes.ACC_PRIVATE, TAIL, CI_DESC, null, null);
        tail.invisibleAnnotations = List.of(mergedAnnotation(origin));
        tail.instructions.add(new MethodInsnNode(Opcodes.INVOKESTATIC, SELF, "unrelatedTail", "()V", false));
        tail.instructions.add(new InsnNode(Opcodes.RETURN)); node.methods.add(tail);
        MethodNode init = new MethodNode(Opcodes.ACC_PUBLIC, "init", "()V", null, null);
        init.instructions.add(new TypeInsnNode(Opcodes.NEW, CI)); init.instructions.add(new InsnNode(Opcodes.DUP));
        init.instructions.add(new LdcInsnNode("init")); init.instructions.add(new InsnNode(Opcodes.ICONST_1));
        init.instructions.add(new MethodInsnNode(Opcodes.INVOKESPECIAL, CI, "<init>", "(Ljava/lang/String;Z)V", false));
        init.instructions.add(new VarInsnNode(Opcodes.ASTORE, 1));
        init.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0)); init.instructions.add(new VarInsnNode(Opcodes.ALOAD, 1));
        init.instructions.add(new MethodInsnNode(Opcodes.INVOKESPECIAL, target, HANDLER, CI_DESC, false));
        init.instructions.add(new VarInsnNode(Opcodes.ALOAD, 1));
        init.instructions.add(new MethodInsnNode(Opcodes.INVOKEVIRTUAL, CI, "isCancelled", "()Z", false));
        LabelNode body = new LabelNode(); init.instructions.add(new JumpInsnNode(Opcodes.IFEQ, body));
        init.instructions.add(new InsnNode(Opcodes.RETURN)); init.instructions.add(body);
        init.instructions.add(new MethodInsnNode(Opcodes.INVOKESTATIC, SELF, "nativeBody", "()V", false));
        init.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0)); init.instructions.add(new VarInsnNode(Opcodes.ALOAD, 1));
        init.instructions.add(new MethodInsnNode(Opcodes.INVOKESPECIAL, target, TAIL, CI_DESC, false));
        init.instructions.add(new InsnNode(Opcodes.RETURN)); node.methods.add(init);
        return write(node);
    }

    private static AnnotationNode mergedAnnotation(String mixin) {
        AnnotationNode annotation = new AnnotationNode("Lorg/spongepowered/asm/mixin/transformer/meta/MixinMerged;");
        annotation.values = new ArrayList<>(List.of("mixin", mixin, "priority", 1000, "sessionId", "offline-shape"));
        return annotation;
    }
    private static String alias(String value, String source) {
        if (value.equals(source)) return target;
        return switch (value) {
            case "net/minecraft/client/Minecraft" -> CLIENT;
            case "net/minecraft/client/server/IntegratedServer" -> SERVER;
            case "net/minecraft/client/gui/screens/Screen" -> SCREEN;
            default -> value;
        };
    }
    private static String descriptor(String value) {
        return value.replace("Lnet/minecraft/client/Minecraft;", "L" + CLIENT + ";")
            .replace("Lnet/minecraft/client/server/IntegratedServer;", "L" + SERVER + ";")
            .replace("Lnet/minecraft/client/gui/screens/Screen;", "L" + SCREEN + ";");
    }
    private static ClassNode read(byte[] bytes) { ClassNode node = new ClassNode(); new ClassReader(bytes).accept(node, 0); return node; }
    private static MethodNode find(ClassNode node, String name) { return node.methods.stream().filter(method -> method.name.equals(name)).findFirst().orElse(null); }
    private static MethodNode cloneMethod(MethodNode method) {
        MethodNode copy = new MethodNode(method.access, method.name, method.desc, method.signature,
            method.exceptions == null ? null : method.exceptions.toArray(String[]::new));
        method.accept(copy); return copy;
    }
    private static byte[] write(ClassNode node) {
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS | ClassWriter.COMPUTE_FRAMES);
        node.accept(writer); return writer.toByteArray();
    }
    private static byte[] transform(byte[] bytes) { return new PortableLanAutoPublishTransformer().transform(null, null, target, null, null, bytes); }
    private static int handlerCalls(byte[] bytes) { return methodCalls(bytes, HANDLER); }
    private static int methodCalls(byte[] bytes, String name) {
        int calls = 0;
        for (MethodNode method : read(bytes).methods) if (method.name.equals("init"))
            for (var instruction : method.instructions) if (instruction instanceof MethodInsnNode call && call.name.equals(name)) calls++;
        return calls;
    }
    private static Screen instantiate(byte[] bytes) throws Exception {
        Loader loader = new Loader();
        loader.defineHelpers();
        Screen screen = (Screen) loader.define(target, bytes).getConstructor().newInstance();
        current = screen; return screen;
    }
    @SuppressWarnings("unchecked")
    private static void request(Screen screen) throws Exception {
        Field requests = PortableLanAutoPublishHooks.class.getDeclaredField("REQUESTED"); requests.setAccessible(true);
        ((Map<Object, Boolean>) requests.get(null)).put(screen, Boolean.TRUE);
    }
    private static void reset() {
        nativeBodies = otherCallbacks = authorScreens = authorRedirects = publishes = closes = 0;
        failPublication = false; current = null; scheduled.clear(); Minecraft.INSTANCE = new Minecraft();
    }
    public static void nativeBody() { nativeBodies++; }
    public static void unrelatedTail() { otherCallbacks++; }
    public static void authorScreen() { authorScreens++; }
    public static void authorRedirect(Object screen) { authorRedirects++; current = screen; }
    private static void configure(boolean world) {
        property("lanPublishEnabled", "true"); property("lanShareScreenClasses", target); property("lanShareInitMethods", "init");
        property("lanPublishExplicitRequestOnly", String.valueOf(world)); property("lanRedirectNativeInitEnabled", "true");
        property("lanRedirectTransportSha256", transportSha); property("lanRedirectMixinOrigin", origin);
        property("minecraftClasses", CLIENT); property("minecraftGetInstanceMethods", "getInstance,m_91087_");
        property("getSingleplayerServerMethods", "getSingleplayerServer,m_91092_"); property("isPublishedMethods", "isPublished");
        property("screenClasses", SCREEN); property("componentClasses", Component.class.getName());
        property("gameTypeClasses", GameType.class.getName()); property("httpUtilClasses", HttpUtil.class.getName());
        property("publishCommandClasses", PublishCommand.class.getName()); property("multiplayerScopeClasses", Scope.class.getName());
        property("lanMultiplayerScopeFields", "LAN"); property("getAvailablePortMethods", "getAvailablePort");
        property("publishServerMethods", "publishServer"); property("setScreenOnGui", "true");
        property("minecraftGuiFields", "gui"); property("setScreenMethods", "setScreen");
        property("minecraftScheduleMethods", "schedule"); property("updateTitleMethods", "updateTitle");
    }
    private static void property(String key, String value) { System.setProperty("minecraft.portable.identity." + key, value); }
    private static void check(boolean condition, String message) { if (!condition) throw new AssertionError(message); }

    public static class Screen {
        public Minecraft minecraft = Minecraft.INSTANCE;
        public Screen lastScreen;
        public void init() { throw new AssertionError("unmerged screen"); }
    }
    public static final class Minecraft implements Executor {
        public static Minecraft INSTANCE;
        public Server server = new Server(); public Gui gui = new Gui();
        public static Minecraft getInstance() { return INSTANCE; }
        public static Minecraft m_91087_() { return INSTANCE; }
        public Server getSingleplayerServer() { return server; }
        public Server m_91092_() { return server; }
        public void schedule(Runnable action) { scheduled.add(action); }
        public void updateTitle() { }
        @Override public void execute(Runnable action) { action.run(); }
    }
    public static final class Gui { public void setScreen(Screen screen) { current = screen; if (screen == null) closes++; } }
    public static final class Server {
        public boolean published;
        public boolean isPublished() { return published; }
        public boolean publishServer(Scope scope, int port) {
            check(nativeBodies > 0 && otherCallbacks > 0, "publication ran before native init/other callbacks");
            publishes++; published = !failPublication; return published;
        }
    }
    public enum Scope { LAN }
    public enum GameType { SURVIVAL }
    public static final class Component { }
    public static final class HttpUtil { public static int getAvailablePort() { return 35658; } }
    public static final class PublishCommand { }

    private static final class Loader extends ClassLoader {
        Class<?> define(String name, byte[] bytes) { return defineClass(name.replace('/', '.'), bytes, 0, bytes.length); }
        void defineHelpers() {
            define("link/e4steam/lan/LanSettingsScreen", settings());
            define("link/e4steam/MinecraftUiCompat", compat());
            define("link/e4steam/lan/WorldOptionsScreenSupport", support());
        }
        byte[] settings() {
            ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
            writer.visit(Opcodes.V17, Opcodes.ACC_PUBLIC, "link/e4steam/lan/LanSettingsScreen", null, SCREEN, null);
            for (String desc : new String[] { "(L" + SCREEN + ";)V", "(L" + SCREEN + ";L" + SCREEN + ";)V" }) {
                MethodVisitor method = writer.visitMethod(Opcodes.ACC_PUBLIC, "<init>", desc, null, null); method.visitCode();
                method.visitVarInsn(Opcodes.ALOAD, 0); method.visitMethodInsn(Opcodes.INVOKESPECIAL, SCREEN, "<init>", "()V", false);
                method.visitMethodInsn(Opcodes.INVOKESTATIC, SELF, "authorScreen", "()V", false);
                method.visitInsn(Opcodes.RETURN); method.visitMaxs(0, 0); method.visitEnd();
            }
            writer.visitEnd(); return writer.toByteArray();
        }
        byte[] compat() {
            ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
            writer.visit(Opcodes.V17, Opcodes.ACC_PUBLIC, "link/e4steam/MinecraftUiCompat", null, "java/lang/Object", null);
            MethodVisitor method = writer.visitMethod(Opcodes.ACC_PUBLIC | Opcodes.ACC_STATIC, "setScreen", "(L" + CLIENT + ";L" + SCREEN + ";)V", null, null);
            method.visitCode(); method.visitVarInsn(Opcodes.ALOAD, 1);
            method.visitMethodInsn(Opcodes.INVOKESTATIC, SELF, "authorRedirect", "(Ljava/lang/Object;)V", false);
            method.visitInsn(Opcodes.RETURN); method.visitMaxs(0, 0); method.visitEnd(); writer.visitEnd(); return writer.toByteArray();
        }
        byte[] support() {
            ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
            writer.visit(Opcodes.V17, Opcodes.ACC_PUBLIC, "link/e4steam/lan/WorldOptionsScreenSupport", null, "java/lang/Object", null);
            MethodVisitor method = writer.visitMethod(Opcodes.ACC_PUBLIC | Opcodes.ACC_STATIC, "consumeNativeWorldTab", "(L" + SCREEN + ";)Z", null, null);
            method.visitCode(); method.visitInsn(Opcodes.ICONST_0); method.visitInsn(Opcodes.IRETURN);
            method.visitMaxs(0, 0); method.visitEnd(); writer.visitEnd(); return writer.toByteArray();
        }
    }
}
