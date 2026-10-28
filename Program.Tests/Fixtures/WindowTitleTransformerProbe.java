import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.HexFormat;
import java.util.zip.ZipFile;
import minecraft.portable.identity.PortableWindowTitleTransformer;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Label;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.LdcInsnNode;
import org.objectweb.asm.tree.MethodNode;

/** Bytecode/lifecycle proof only; this does not claim a rendered Minecraft session. */
public final class WindowTitleTransformerProbe implements Opcodes {
    private static final String PREFIX = "minecraft.portable.identity.";
    private static final String CLIENT = "fixture/WindowTitleClient";
    private static final String TITLE = "Caves and Dungeons";

    public static void main(String[] arguments) throws Exception {
        if (arguments.length == 0) {
            synthetic();
            System.out.println("WINDOW_TITLE_SYNTHETIC_PASS");
            return;
        }
        if (arguments.length == 1 && arguments[0].equals("--premain")) {
            require(!Boolean.getBoolean(PREFIX + "enabled"), "premain proof requires identity.enabled=false");
            byte[] original = fixture();
            configure(CLIENT, "createTitle", TITLE, false);
            Class<?> before = new ByteLoader().define(original);
            configure(CLIENT, "createTitle", TITLE, true);
            // Deliberately raw bytes: only the registered javaagent can make
            // this second class return the fixed title. No manual transform.
            Class<?> after = new ByteLoader().define(original);
            verifyLifecycle(before, after);
            System.out.println("WINDOW_TITLE_PREMAIN_PASS");
            return;
        }
        if (arguments.length < 3 || arguments.length > 4)
            throw new IllegalArgumentException("Usage: [<runtime-jar> <internal-class-name> <producer-name> [title|--disabled]]");
        String className = arguments[1].replace('.', '/');
        String method = arguments[2];
        boolean enabled = arguments.length != 4 || !arguments[3].equals("--disabled");
        String title = arguments.length == 4 && enabled ? arguments[3] : TITLE;
        byte[] original;
        try (ZipFile jar = new ZipFile(Path.of(arguments[0]).toFile())) {
            var entry = jar.getEntry(className + ".class");
            require(entry != null, "native target class is absent");
            try (var stream = jar.getInputStream(entry)) { original = stream.readAllBytes(); }
        }
        ClassNode nativeTarget = new ClassNode();
        new ClassReader(original).accept(nativeTarget, 0);
        require(className.equals(nativeTarget.name), "native class name differs from target");
        long nativeProducers = nativeTarget.methods.stream().filter(candidate ->
            candidate.name.equals(method) && candidate.desc.equals("()Ljava/lang/String;") &&
            (candidate.access & (ACC_STATIC | ACC_ABSTRACT | ACC_NATIVE)) == 0).count();
        require(nativeProducers == 1, "native producer does not have the exact instance String signature");
        configure(className, method, title, enabled);
        byte[] patched = transform(className, original);
        if (enabled) {
            require(patched != null, "enabled native producer was not transformed");
            verifyOnlyProducerChanged(original, patched, method, title);
        } else {
            require(patched == null || Arrays.equals(original, patched), "disabled target was changed");
        }
        System.out.println("NATIVE_CLASS_SHA256=" + HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(original)));
        System.out.println("CHANGED_METHOD_COUNT=" + (enabled ? 1 : 0));
        System.out.println((enabled ? "WINDOW_TITLE_NATIVE_PASS|" : "WINDOW_TITLE_NATIVE_DISABLED_PASS|") + className + "|" + method);
    }

    private static void synthetic() throws Exception {
        byte[] original = fixture();
        configure(CLIENT, "createTitle", TITLE, false);
        require(transform(CLIENT, original) == null, "disabled fixture was changed");
        configure(CLIENT, "createTitle", TITLE, true);
        require(transform("another/Client", original) == null, "unconfigured class was changed");

        // The cosmetic transform also works when the unrelated identity and
        // LAN families are unavailable. Its replacement calls no hooks.
        System.setProperty(PREFIX + "enabled", "false");
        System.setProperty(PREFIX + "lanPublishEnabled", "false");
        byte[] patched = transform(CLIENT, original);
        require(patched != null, "title depends on identity/LAN hooks");
        verifyOnlyProducerChanged(original, patched, "createTitle", TITLE);
        Class<?> before = new ByteLoader().define(original);
        Class<?> after = new ByteLoader().define(patched);
        verifyLifecycle(before, after);

        configure(CLIENT, "missingProducer", TITLE, true);
        expectFailure(original, "missing producer");
        configure(CLIENT, "createTitle,anotherTitle", TITLE, true);
        expectFailure(original, "ambiguous producers");
        configure(CLIENT, "helperTitle", TITLE, true);
        expectFailure(original, "static producer");
        configure(CLIENT, "createTitle", " ", true);
        expectFailure(original, "empty title");
        configure(CLIENT, "createTitle", TITLE, true);
        System.setProperty(PREFIX + "createTitleDescriptors", "()I");
        expectFailure(original, "wrong descriptor");
    }

    private static void verifyLifecycle(Class<?> before, Class<?> after) throws Exception {
        verifyLifecycle(before, after, null);
        verifyLifecycle(before, after, "FancyMenu / KubeJS / Minecraft 1.20.1 / Steam / LAN");
    }

    private static void verifyLifecycle(Class<?> before, Class<?> after, String providerCaption) throws Exception {
        Object baseline = before.getConstructor().newInstance();
        Object actual = after.getConstructor().newInstance();
        before.getField("providerCaption").set(baseline, providerCaption);
        after.getField("providerCaption").set(actual, providerCaption);
        String[] states = { "menu", "Singleplayer", "LAN", "Steam", "Multiplayer", "participant joined", "participant left", "disconnected", "menu" };
        for (String state : states) {
            before.getMethod("setState", String.class).invoke(baseline, state);
            after.getMethod("setState", String.class).invoke(actual, state);
            require(TITLE.equals(after.getField("caption").get(actual)), "state returned to caption: " + state);
            String baselineCaption = (String) before.getField("caption").get(baseline);
            if (providerCaption == null)
                require(baselineCaption.endsWith(state), "baseline did not exercise a changing title");
            else
                require(providerCaption.equals(baselineCaption), "baseline did not exercise the injected HEAD provider");
        }
        for (String event : new String[] { "publishLan", "joinPeer", "peerChanged", "disconnect" }) {
            Object beforeResult = before.getMethod(event).invoke(baseline);
            Object afterResult = after.getMethod(event).invoke(actual);
            require(java.util.Objects.equals(beforeResult, afterResult), "event result changed: " + event);
            require(TITLE.equals(after.getField("caption").get(actual)), "event put a state back: " + event);
        }
        for (String counter : new String[] { "updates", "publications", "joins", "peerChanges", "disconnects" })
            require(before.getField(counter).getInt(baseline) == after.getField(counter).getInt(actual), "event side effect changed: " + counter);
        require("overload".equals(after.getMethod("createTitle", int.class).invoke(actual, 1)), "String overload was changed");
        require("unchanged".equals(after.getMethod("helperTitle").invoke(null)), "static helper was changed");
        require(java.util.Objects.equals(providerCaption, after.getMethod("providerHook").invoke(actual)), "provider hook behavior was changed");

    }

    private static void expectFailure(byte[] original, String scenario) throws Exception {
        try { transform(CLIENT, original); }
        catch (IllegalStateException expected) { return; }
        throw new AssertionError("invalid configuration accepted: " + scenario);
    }

    private static void configure(String className, String method, String title, boolean enabled) {
        System.setProperty(PREFIX + "windowTitleEnabled", Boolean.toString(enabled));
        System.setProperty(PREFIX + "windowTitleClasses", className);
        System.setProperty(PREFIX + "createTitleMethods", method);
        System.setProperty(PREFIX + "createTitleDescriptors", "()Ljava/lang/String;");
        System.setProperty(PREFIX + "windowTitleText", title);
    }

    private static byte[] transform(String className, byte[] original) throws Exception {
        return new PortableWindowTitleTransformer().transform(null, null, className, null, null, original);
    }

    private static void verifyOnlyProducerChanged(byte[] original, byte[] transformed, String producer, String title) {
        ClassNode before = new ClassNode();
        new ClassReader(original).accept(before, 0);
        ClassNode after = new ClassNode();
        new ClassReader(transformed).accept(after, 0);
        require(before.name.equals(after.name), "class identity changed");
        require(before.methods.size() == after.methods.size(), "method count changed");
        int changed = 0;
        for (int i = 0; i < before.methods.size(); i++) {
            MethodNode old = before.methods.get(i), now = after.methods.get(i);
            require(old.name.equals(now.name) && old.desc.equals(now.desc) && old.access == now.access, "method identity changed");
            if (Arrays.equals(methodBytes(old), methodBytes(now))) continue;
            changed++;
            require(now.name.equals(producer) && now.desc.equals("()Ljava/lang/String;"), "non-producer changed: " + now.name);
            AbstractInsnNode first = now.instructions.getFirst();
            require(first instanceof LdcInsnNode && title.equals(((LdcInsnNode) first).cst), "producer is not configured constant");
            require(first.getNext() != null && first.getNext().getOpcode() == ARETURN && first.getNext().getNext() == null, "producer has other behavior");
            require(now.tryCatchBlocks.isEmpty(), "producer kept an old exception range");
        }
        require(changed == 1, "expected one changed producer, saw " + changed);
    }

    private static byte[] methodBytes(MethodNode method) {
        ClassWriter writer = new ClassWriter(0);
        writer.visit(V17, ACC_PUBLIC, "PortableTitleMethodProof", null, "java/lang/Object", null);
        method.accept(writer);
        writer.visitEnd();
        return writer.toByteArray();
    }

    private static byte[] fixture() {
        ClassWriter c = new ClassWriter(ClassWriter.COMPUTE_MAXS | ClassWriter.COMPUTE_FRAMES);
        c.visit(V17, ACC_PUBLIC, CLIENT, null, "java/lang/Object", null);
        for (String name : new String[] { "state", "caption", "providerCaption" }) c.visitField(ACC_PUBLIC, name, "Ljava/lang/String;", null, null).visitEnd();
        for (String name : new String[] { "updates", "publications", "joins", "peerChanges", "disconnects" }) c.visitField(ACC_PUBLIC, name, "I", null, null).visitEnd();
        MethodVisitor m = c.visitMethod(ACC_PUBLIC, "<init>", "()V", null, null);
        m.visitCode(); m.visitVarInsn(ALOAD, 0); m.visitMethodInsn(INVOKESPECIAL, "java/lang/Object", "<init>", "()V", false);
        m.visitVarInsn(ALOAD, 0); m.visitLdcInsn("menu"); m.visitFieldInsn(PUTFIELD, CLIENT, "state", "Ljava/lang/String;"); m.visitInsn(RETURN); finish(m);
        m = c.visitMethod(ACC_PRIVATE, "createTitle", "()Ljava/lang/String;", null, null);
        m.visitCode();
        // Model a loader/Mixin HEAD callback which returns an author's title
        // before vanilla builds its own caption. The VM transformer receives
        // this final producer bytecode, not an earlier untransformed body.
        Label vanillaFallback = new Label();
        m.visitVarInsn(ALOAD, 0); m.visitMethodInsn(INVOKEVIRTUAL, CLIENT, "providerHook", "()Ljava/lang/String;", false);
        m.visitInsn(DUP); m.visitJumpInsn(IFNULL, vanillaFallback); m.visitInsn(ARETURN);
        m.visitLabel(vanillaFallback); m.visitInsn(POP);
        m.visitTypeInsn(NEW, "java/lang/StringBuilder"); m.visitInsn(DUP); m.visitLdcInsn("Minecraft Forge* 1.20.1 - ");
        m.visitMethodInsn(INVOKESPECIAL, "java/lang/StringBuilder", "<init>", "(Ljava/lang/String;)V", false);
        m.visitVarInsn(ALOAD, 0); m.visitFieldInsn(GETFIELD, CLIENT, "state", "Ljava/lang/String;");
        m.visitMethodInsn(INVOKEVIRTUAL, "java/lang/StringBuilder", "append", "(Ljava/lang/String;)Ljava/lang/StringBuilder;", false);
        m.visitMethodInsn(INVOKEVIRTUAL, "java/lang/StringBuilder", "toString", "()Ljava/lang/String;", false); m.visitInsn(ARETURN); finish(m);
        m = c.visitMethod(ACC_PUBLIC, "providerHook", "()Ljava/lang/String;", null, null);
        m.visitCode(); m.visitVarInsn(ALOAD, 0); m.visitFieldInsn(GETFIELD, CLIENT, "providerCaption", "Ljava/lang/String;");
        m.visitInsn(ARETURN); finish(m);
        m = c.visitMethod(ACC_PUBLIC, "updateTitle", "()V", null, null);
        m.visitCode(); increment(m, "updates"); m.visitVarInsn(ALOAD, 0); m.visitVarInsn(ALOAD, 0);
        m.visitMethodInsn(INVOKEVIRTUAL, CLIENT, "createTitle", "()Ljava/lang/String;", false);
        m.visitFieldInsn(PUTFIELD, CLIENT, "caption", "Ljava/lang/String;"); m.visitInsn(RETURN); finish(m);
        m = c.visitMethod(ACC_PUBLIC, "setState", "(Ljava/lang/String;)V", null, null);
        m.visitCode(); m.visitVarInsn(ALOAD, 0); m.visitVarInsn(ALOAD, 1); m.visitFieldInsn(PUTFIELD, CLIENT, "state", "Ljava/lang/String;");
        m.visitVarInsn(ALOAD, 0); m.visitMethodInsn(INVOKEVIRTUAL, CLIENT, "updateTitle", "()V", false); m.visitInsn(RETURN); finish(m);
        event(c, "publishLan", "publications", "LAN", true);
        event(c, "joinPeer", "joins", "Steam", false);
        event(c, "peerChanged", "peerChanges", "Multiplayer", false);
        event(c, "disconnect", "disconnects", "menu", false);
        constant(c, ACC_PUBLIC, "createTitle", "(I)Ljava/lang/String;", "overload");
        constant(c, ACC_PRIVATE, "anotherTitle", "()Ljava/lang/String;", "another");
        constant(c, ACC_PUBLIC | ACC_STATIC, "helperTitle", "()Ljava/lang/String;", "unchanged");
        c.visitEnd(); return c.toByteArray();
    }

    private static void increment(MethodVisitor m, String field) {
        m.visitVarInsn(ALOAD, 0); m.visitInsn(DUP); m.visitFieldInsn(GETFIELD, CLIENT, field, "I");
        m.visitInsn(ICONST_1); m.visitInsn(IADD); m.visitFieldInsn(PUTFIELD, CLIENT, field, "I");
    }
    private static void event(ClassWriter c, String name, String counter, String state, boolean result) {
        MethodVisitor m = c.visitMethod(ACC_PUBLIC, name, result ? "()Z" : "()V", null, null);
        m.visitCode(); increment(m, counter); m.visitVarInsn(ALOAD, 0); m.visitLdcInsn(state);
        m.visitMethodInsn(INVOKEVIRTUAL, CLIENT, "setState", "(Ljava/lang/String;)V", false);
        if (result) { m.visitInsn(ICONST_1); m.visitInsn(IRETURN); } else m.visitInsn(RETURN); finish(m);
    }
    private static void constant(ClassWriter c, int access, String name, String desc, String value) {
        MethodVisitor m = c.visitMethod(access, name, desc, null, null);
        m.visitCode(); m.visitLdcInsn(value); m.visitInsn(ARETURN); finish(m);
    }
    private static void finish(MethodVisitor method) { method.visitMaxs(0, 0); method.visitEnd(); }
    private static void require(boolean condition, String message) { if (!condition) throw new AssertionError(message); }
    private static final class ByteLoader extends ClassLoader {
        Class<?> define(byte[] bytes) { return defineClass(CLIENT.replace('/', '.'), bytes, 0, bytes.length); }
    }
}
