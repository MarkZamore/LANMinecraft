import java.io.IOException;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.ConcurrentModificationException;
import java.util.HashMap;
import java.util.HexFormat;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.IntSupplier;
import java.util.jar.JarFile;
import minecraft.portable.identity.PortableForgeHandshakeTransformer;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.ConstantDynamic;
import org.objectweb.asm.Handle;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.*;

/** Structural rejection and deterministic concurrent execution of the guarded native list operations. */
public final class ForgeHandshakeProbe {
    private static final String TARGET = PortableForgeHandshakeTransformer.TARGET;
    private static final String PROJECTED = "probe/IsolatedForgeHandshake";
    private static final String LIST = "Ljava/util/List;";
    private static final String FLAG = "minecraft.portable.identity.forgeHandshakeSyncEnabled";
    private static final String NATIVE_SHA = "2b0e97b22fd4d043dbbcc8e0a44ef0d58915689ed9224e4cd28d5c4976a16bcc";
    private static int checks;

    public static void main(String[] args) throws Exception {
        byte[] original = fixture();
        System.setProperty(FLAG, "false");
        require(transform(original) == null, "disabled feature changed a class");
        System.setProperty(FLAG, "true");
        require(new PortableForgeHandshakeTransformer().transform(null, null, "other/Handler", null, null, original) == null,
            "unrelated class was transformed");
        byte[] fixed = transform(original);
        require(fixed != null, "fixture not transformed");
        PortableForgeHandshakeTransformer.verify(original, fixed);
        require(transform(fixed) == null, "second transform must not double-wrap");
        expectRejected("ambiguous initializer", mutate(original, node -> {
            MethodNode ctor = method(node, "<init>");
            ctor.instructions.insertBefore(ctor.instructions.getLast(), initialize("sentMessages"));
        }));
        expectRejected("wrong field type", mutate(original, node -> {
            node.fields.stream().filter(f -> f.name.equals("sentMessages")).forEach(f -> f.desc = "Ljava/util/ArrayList;");
            for (MethodNode method : node.methods) for (AbstractInsnNode insn : method.instructions)
                if (insn instanceof FieldInsnNode field && field.name.equals("sentMessages")) field.desc = "Ljava/util/ArrayList;";
        }));
        expectRejected("escaped list", mutate(original, node -> {
            MethodNode escape = addMethod(node, Opcodes.ACC_PUBLIC, "escape", "()Ljava/util/List;");
            list(escape); escape.instructions.add(new InsnNode(Opcodes.ARETURN));
        }));
        expectRejected("unsupported list consumer", mutate(original, node -> {
            for (MethodNode method : node.methods) for (AbstractInsnNode insn : method.instructions)
                if (insn instanceof MethodInsnNode call && call.owner.equals("java/util/List") && call.name.equals("contains"))
                    call.name = "remove";
        }));
        expectRejected("writer outside constructor", mutate(original, node -> {
            MethodNode reset = addMethod(node, Opcodes.ACC_PUBLIC, "reset", "()V");
            reset.instructions.add(initialize("sentMessages")); reset.instructions.add(new InsnNode(Opcodes.RETURN));
        }));
        expectRejected("unreviewed predicate capture", mutate(original, node -> {
            for (AbstractInsnNode insn : method(node, "handleIndexedMessage").instructions)
                if (insn instanceof InvokeDynamicInsnNode indy) indy.desc = "(Ljava/util/List;)Ljava/util/function/Predicate;";
        }));
        expectRejected("foreign predicate implementation", mutate(original, node -> {
            for (AbstractInsnNode insn : method(node, "handleIndexedMessage").instructions)
                if (insn instanceof InvokeDynamicInsnNode indy && indy.bsmArgs[1] instanceof Handle h)
                    indy.bsmArgs[1] = new Handle(h.getTag(), "foreign/Predicate", h.getName(), h.getDesc(), false);
        }));
        expectRejected("predicate with additional work", mutate(original, node -> {
            InsnList work = new InsnList();
            work.add(new MethodInsnNode(Opcodes.INVOKESTATIC, "java/lang/System", "nanoTime", "()J", false));
            work.add(new InsnNode(Opcodes.POP2)); method(node, "nativePredicate").instructions.insert(work);
        }));
        expectRejected("field handle escape", mutate(original, node -> {
            InsnList escape = new InsnList();
            escape.add(new LdcInsnNode(new Handle(Opcodes.H_GETFIELD, TARGET, "sentMessages", LIST, false)));
            escape.add(new InsnNode(Opcodes.POP)); method(node, "untouched").instructions.insert(escape);
        }));
        expectRejected("field handle in dynamic constant", mutate(original, node -> {
            Handle bootstrap = new Handle(Opcodes.H_INVOKESTATIC, "java/lang/invoke/ConstantBootstraps", "explicitCast",
                "(Ljava/lang/invoke/MethodHandles$Lookup;Ljava/lang/String;Ljava/lang/Class;Ljava/lang/Object;)Ljava/lang/Object;", false);
            InsnList escape = new InsnList();
            escape.add(new LdcInsnNode(new ConstantDynamic("escaped", "Ljava/lang/invoke/MethodHandle;", bootstrap,
                new Handle(Opcodes.H_GETFIELD, TARGET, "sentMessages", LIST, false))));
            escape.add(new InsnNode(Opcodes.POP)); method(node, "untouched").instructions.insert(escape);
        }));
        byte[] tampered = mutate(fixed, node -> method(node, "untouched").instructions.insert(new InsnNode(Opcodes.NOP)));
        try {
            PortableForgeHandshakeTransformer.verify(original, tampered);
            throw new AssertionError("preflight accepted an unrelated method change");
        } catch (IllegalStateException expected) { checks++; }
        behavior("fixture", original, fixed);
        if (args.length > 1) throw new IllegalArgumentException("Usage: ForgeHandshakeProbe [forge-1.20.1-47.4.0-universal.jar]");
        if (args.length == 1) {
            Path jar = Path.of(args[0]);
            String sha = HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(jar)));
            require(sha.equals(NATIVE_SHA), "native input is not reviewed Forge 47.4.0: " + sha);
            byte[] nativeClass;
            try (JarFile archive = new JarFile(jar.toFile())) {
                var entry = archive.getJarEntry(TARGET + ".class");
                if (entry == null) throw new IOException("Native target absent");
                try (var stream = archive.getInputStream(entry)) { nativeClass = stream.readAllBytes(); }
            }
            byte[] patched = transform(nativeClass);
            require(patched != null, "native class not transformed");
            PortableForgeHandshakeTransformer.verify(nativeClass, patched);
            require(transform(patched) == null, "native second transform must not double-wrap");
            behavior("native47.4.0", nativeClass, patched);
            System.out.println("NATIVE INPUT SHA256 " + sha);
            System.out.println("Native execution scope: isolated exact constructor/list-operation bytecode; no Minecraft world or real two-client connection.");
        }
        System.out.println("PASS Forge handshake: " + checks + " checks; deterministic original CME, synchronized ACK/add, unknown ACK, pending reply and per-connection isolation.");
    }

    private static byte[] transform(byte[] bytes) {
        return new PortableForgeHandshakeTransformer().transform(null, ForgeHandshakeProbe.class.getClassLoader(), TARGET, null, null, bytes);
    }

    private static void expectRejected(String name, byte[] source) {
        try {
            byte[] result = transform(source);
            if (result != null) throw new AssertionError(name + " was transformed");
            throw new AssertionError(name + " was silently accepted");
        } catch (IllegalStateException expected) { checks++; }
    }

    private static void behavior(String label, byte[] original, byte[] fixed) throws Exception {
        Class<?> oldType = new Loader().define(project(original));
        Class<?> type = new Loader().define(project(fixed));
        race(label + " original", oldType, false);
        race(label + " fixed", type, true);
        Object pending = create(type); offer(pending, 7);
        require(!(boolean)invoke(pending, "ack", new Class<?>[]{IntSupplier.class}, (IntSupplier)() -> 99), label + " unexpected ACK matched");
        require(!(boolean)invoke(pending, "empty", new Class<?>[0]), label + " unexpected ACK completed pending handshake");
        require((boolean)invoke(pending, "contains", new Class<?>[]{int.class}, 7), label + " pending index lost");
        RuntimeException failure = new RuntimeException("probe predicate failure");
        try {
            invoke(pending, "ack", new Class<?>[]{IntSupplier.class}, (IntSupplier)() -> { throw failure; });
            throw new AssertionError("predicate exception swallowed");
        } catch (RuntimeException expected) { require(expected == failure, label + " predicate exception changed"); }
        require((boolean)invoke(pending, "ack", new Class<?>[]{IntSupplier.class}, (IntSupplier)() -> 7), label + " correct ACK not consumed");
        require((boolean)invoke(pending, "empty", new Class<?>[0]), label + " fully ACKed list not empty");
        independentConnections(type, label);
    }

    private static void race(String label, Class<?> type, boolean synchronizedList) throws Exception {
        Object handler = create(type); offer(handler, 1); setPosition(handler, 2);
        CountDownLatch predicateEntered = new CountDownLatch(1), release = new CountDownLatch(1), addAttempt = new CountDownLatch(1), addDone = new CountDownLatch(1);
        AtomicReference<Throwable> ackFailure = new AtomicReference<>(), addFailure = new AtomicReference<>();
        Thread ack = new Thread(() -> {
            try { invoke(handler, "ack", new Class<?>[]{IntSupplier.class}, (IntSupplier)() -> {
                predicateEntered.countDown(); await(release, "predicate release"); return 1;
            }); } catch (Throwable failure) { ackFailure.set(failure); }
        }, "probe-forge-ack");
        Thread add = new Thread(() -> {
            addAttempt.countDown();
            try { invoke(handler, "offer", new Class<?>[0]); }
            catch (Throwable failure) { addFailure.set(failure); }
            finally { addDone.countDown(); }
        }, "probe-forge-tick");
        try {
            ack.start(); await(predicateEntered, label + " predicate start"); add.start(); await(addAttempt, label + " add attempt");
            if (synchronizedList) {
                long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
                while (add.getState() != Thread.State.BLOCKED && addDone.getCount() != 0 && System.nanoTime() < deadline) Thread.yield();
                require(add.getState() == Thread.State.BLOCKED && addDone.getCount() == 1, label + " add did not wait for ACK's list monitor");
            } else await(addDone, label + " concurrent add");
        } finally {
            release.countDown(); ack.join(5000); if (add.getState() != Thread.State.NEW) add.join(5000);
        }
        require(!ack.isAlive() && !add.isAlive(), label + " worker did not finish");
        require(addFailure.get() == null, label + " add failed: " + addFailure.get());
        if (synchronizedList) {
            require(ackFailure.get() == null, label + " ACK failed: " + ackFailure.get());
            require(!(boolean)invoke(handler, "contains", new Class<?>[]{int.class}, 1), label + " old ACK not removed");
            require((boolean)invoke(handler, "contains", new Class<?>[]{int.class}, 2), label + " concurrent pending reply lost");
            require(!(boolean)invoke(handler, "empty", new Class<?>[0]), label + " handshake completed before new reply");
        } else require(ackFailure.get() instanceof ConcurrentModificationException, label + " did not reproduce CME: " + ackFailure.get());
    }

    private static void independentConnections(Class<?> type, String label) throws Exception {
        Object left = create(type), right = create(type); offer(left, 1);
        CountDownLatch entered = new CountDownLatch(1), release = new CountDownLatch(1), otherDone = new CountDownLatch(1);
        AtomicReference<Throwable> failure = new AtomicReference<>();
        Thread blocked = new Thread(() -> {
            try { invoke(left, "ack", new Class<?>[]{IntSupplier.class}, (IntSupplier)() -> { entered.countDown(); await(release, "independent release"); return 1; }); }
            catch (Throwable e) { failure.set(e); }
        });
        Thread other = new Thread(() -> {
            try { offer(right, 2); } catch (Throwable e) { failure.set(e); } finally { otherDone.countDown(); }
        });
        try { blocked.start(); await(entered, "independent predicate"); other.start(); await(otherDone, label + " unrelated connection blocked"); }
        finally { release.countDown(); blocked.join(5000); if (other.getState() != Thread.State.NEW) other.join(5000); }
        require(failure.get() == null, label + " unrelated connection error: " + failure.get());
    }

    private static void await(CountDownLatch latch, String name) {
        try { if (!latch.await(5, TimeUnit.SECONDS)) throw new AssertionError("Timed out: " + name); }
        catch (InterruptedException e) { Thread.currentThread().interrupt(); throw new AssertionError(e); }
    }
    private static Object create(Class<?> type) throws Exception { return type.getConstructor().newInstance(); }
    private static void setPosition(Object handler, int value) throws Exception { var field = handler.getClass().getDeclaredField("packetPosition"); field.setAccessible(true); field.setInt(handler, value); }
    private static void offer(Object handler, int value) throws Exception { setPosition(handler, value); invoke(handler, "offer", new Class<?>[0]); }
    private static Object invoke(Object object, String name, Class<?>[] types, Object... args) throws Exception {
        try { return object.getClass().getMethod(name, types).invoke(object, args); }
        catch (InvocationTargetException e) {
            if (e.getCause() instanceof Exception exception) throw exception;
            if (e.getCause() instanceof Error error) throw error;
            throw e;
        }
    }
    private static void require(boolean value, String message) { if (!value) throw new AssertionError(message); checks++; }
    private static class Loader extends ClassLoader { Class<?> define(byte[] bytes) { return defineClass(null, bytes, 0, bytes.length); } }

    /* Project only the actual native initializer, List calls and predicate. Forge logging,
       networking and world bootstrap are outside this test's execution boundary. */
    private static byte[] project(byte[] source) {
        ClassNode input = read(source), output = new ClassNode();
        output.version = Opcodes.V17; output.access = Opcodes.ACC_PUBLIC; output.name = PROJECTED; output.superName = "java/lang/Object";
        output.fields.add(new FieldNode(Opcodes.ACC_PRIVATE, "sentMessages", LIST, null, null));
        output.fields.add(new FieldNode(Opcodes.ACC_PRIVATE, "packetPosition", "I", null, null));
        MethodNode ctor = addMethod(output, Opcodes.ACC_PUBLIC, "<init>", "()V");
        ctor.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0)); ctor.instructions.add(new MethodInsnNode(Opcodes.INVOKESPECIAL, "java/lang/Object", "<init>", "()V", false));
        MethodNode inputCtor = method(input, "<init>");
        FieldInsnNode write = Arrays.stream(inputCtor.instructions.toArray()).filter(i -> i instanceof FieldInsnNode f && f.getOpcode() == Opcodes.PUTFIELD && f.name.equals("sentMessages")).map(i -> (FieldInsnNode)i).findFirst().orElseThrow();
        AbstractInsnNode start = write;
        while (!(start instanceof TypeInsnNode type && type.getOpcode() == Opcodes.NEW && type.desc.equals("java/util/ArrayList"))) start = start.getPrevious();
        start = previousReal(start); require(start instanceof VarInsnNode load && load.getOpcode() == Opcodes.ALOAD && load.var == 0, "native initializer receiver changed");
        copyRange(ctor, start, write); ctor.instructions.add(new InsnNode(Opcodes.RETURN));
        for (String[] spec : new String[][]{{"removeIf", "ack", "(Ljava/util/function/IntSupplier;)Z"}, {"add", "offer", "()Z"}, {"isEmpty", "empty", "()Z"}, {"contains", "contains", "(I)Z"}}) {
            MethodInsnNode consumer = null;
            for (MethodNode m : input.methods) for (AbstractInsnNode i : m.instructions)
                if (i instanceof MethodInsnNode call && call.owner.equals("java/util/List") && call.name.equals(spec[0])) {
                    AbstractInsnNode candidate = receiverField(call);
                    if (candidate instanceof FieldInsnNode f && f.name.equals("sentMessages")) {
                        if (consumer != null) throw new AssertionError("ambiguous projected native consumer " + spec[0]); consumer = call;
                    }
                }
            if (consumer == null) throw new AssertionError("native consumer absent " + spec[0]);
            FieldInsnNode field = (FieldInsnNode)receiverField(consumer);
            MethodNode projected = addMethod(output, Opcodes.ACC_PUBLIC, spec[1], spec[2]);
            copyRange(projected, previousReal(field), consumer);
            // contains() starts with native local2, the projected holder is this/local0.
            ((VarInsnNode)projected.instructions.getFirst()).var = 0;
            projected.instructions.add(new InsnNode(Opcodes.IRETURN));
            for (AbstractInsnNode i : projected.instructions)
                if (i instanceof InvokeDynamicInsnNode indy) for (Object arg : indy.bsmArgs)
                    if (arg instanceof Handle handle && handle.getOwner().equals(PROJECTED)) {
                        MethodNode lambda = input.methods.stream().filter(m -> m.name.equals(handle.getName()) && m.desc.equals(handle.getDesc())).findFirst().orElseThrow();
                        if (output.methods.stream().noneMatch(m -> m.name.equals(lambda.name))) {
                            MethodNode copied = addMethod(output, lambda.access, lambda.name, lambda.desc);
                            copyRange(copied, lambda.instructions.getFirst(), lambda.instructions.getLast());
                        }
                    }
        }
        return write(output, true);
    }

    private static AbstractInsnNode receiverField(AbstractInsnNode consumer) {
        for (AbstractInsnNode i = consumer.getPrevious(); i != null; i = i.getPrevious())
            if (i instanceof FieldInsnNode field && field.getOpcode() == Opcodes.GETFIELD && field.desc.equals(LIST)) return field;
        return null;
    }
    private static AbstractInsnNode previousReal(AbstractInsnNode i) { do { i = i.getPrevious(); } while (i != null && i.getOpcode() < 0); return i; }
    private static void copyRange(MethodNode to, AbstractInsnNode first, AbstractInsnNode last) {
        Map<LabelNode, LabelNode> labels = new HashMap<>();
        for (AbstractInsnNode i = first; i != null; i = i.getNext()) { if (i instanceof LabelNode label) labels.put(label, new LabelNode()); if (i == last) break; }
        for (AbstractInsnNode i = first; i != null; i = i.getNext()) {
            if (!(i instanceof FrameNode) && !(i instanceof LineNumberNode)) {
                AbstractInsnNode cloned = i.clone(labels);
                if (cloned instanceof FieldInsnNode field && field.owner.equals(TARGET)) field.owner = PROJECTED;
                if (cloned instanceof InvokeDynamicInsnNode indy) for (int index = 0; index < indy.bsmArgs.length; index++)
                    if (indy.bsmArgs[index] instanceof Handle h && h.getOwner().equals(TARGET)) indy.bsmArgs[index] = new Handle(h.getTag(), PROJECTED, h.getName(), h.getDesc(), h.isInterface());
                to.instructions.add(cloned);
            }
            if (i == last) break;
        }
    }

    private static byte[] fixture() {
        ClassNode node = new ClassNode(); node.version = Opcodes.V17; node.access = Opcodes.ACC_PUBLIC; node.name = TARGET; node.superName = "java/lang/Object";
        node.fields.add(new FieldNode(Opcodes.ACC_PRIVATE, "sentMessages", LIST, null, null));
        node.fields.add(new FieldNode(Opcodes.ACC_PRIVATE, "pendingFutures", LIST, null, null));
        node.fields.add(new FieldNode(Opcodes.ACC_PRIVATE, "packetPosition", "I", null, null));
        MethodNode ctor = addMethod(node, Opcodes.ACC_PUBLIC, "<init>", "()V");
        ctor.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0)); ctor.instructions.add(new MethodInsnNode(Opcodes.INVOKESPECIAL, "java/lang/Object", "<init>", "()V", false));
        ctor.instructions.add(initialize("sentMessages")); ctor.instructions.add(initialize("pendingFutures")); ctor.instructions.add(new InsnNode(Opcodes.RETURN));
        MethodNode ack = addMethod(node, Opcodes.ACC_PUBLIC, "handleIndexedMessage", "(Ljava/util/function/IntSupplier;Ljava/util/function/Supplier;)V");
        list(ack); ack.instructions.add(new VarInsnNode(Opcodes.ALOAD, 1));
        Handle meta = new Handle(Opcodes.H_INVOKESTATIC, "java/lang/invoke/LambdaMetafactory", "metafactory", "(Ljava/lang/invoke/MethodHandles$Lookup;Ljava/lang/String;Ljava/lang/invoke/MethodType;Ljava/lang/invoke/MethodType;Ljava/lang/invoke/MethodHandle;Ljava/lang/invoke/MethodType;)Ljava/lang/invoke/CallSite;", false);
        ack.instructions.add(new InvokeDynamicInsnNode("test", "(Ljava/util/function/IntSupplier;)Ljava/util/function/Predicate;", meta,
            org.objectweb.asm.Type.getMethodType("(Ljava/lang/Object;)Z"), new Handle(Opcodes.H_INVOKESTATIC, TARGET, "nativePredicate", "(Ljava/util/function/IntSupplier;Ljava/lang/Integer;)Z", false), org.objectweb.asm.Type.getMethodType("(Ljava/lang/Integer;)Z")));
        ack.instructions.add(new MethodInsnNode(Opcodes.INVOKEINTERFACE, "java/util/List", "removeIf", "(Ljava/util/function/Predicate;)Z", true)); ack.instructions.add(new InsnNode(Opcodes.POP)); ack.instructions.add(new InsnNode(Opcodes.RETURN));
        MethodNode predicate = addMethod(node, Opcodes.ACC_PRIVATE | Opcodes.ACC_STATIC | Opcodes.ACC_SYNTHETIC, "nativePredicate", "(Ljava/util/function/IntSupplier;Ljava/lang/Integer;)Z");
        predicate.instructions.add(new VarInsnNode(Opcodes.ALOAD, 1)); predicate.instructions.add(new MethodInsnNode(Opcodes.INVOKEVIRTUAL, "java/lang/Integer", "intValue", "()I", false));
        predicate.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0)); predicate.instructions.add(new MethodInsnNode(Opcodes.INVOKEINTERFACE, "java/util/function/IntSupplier", "getAsInt", "()I", true));
        LabelNode no = new LabelNode(), end = new LabelNode(); predicate.instructions.add(new JumpInsnNode(Opcodes.IF_ICMPNE, no)); predicate.instructions.add(new InsnNode(Opcodes.ICONST_1)); predicate.instructions.add(new JumpInsnNode(Opcodes.GOTO, end)); predicate.instructions.add(no); predicate.instructions.add(new InsnNode(Opcodes.ICONST_0)); predicate.instructions.add(end); predicate.instructions.add(new InsnNode(Opcodes.IRETURN));
        MethodNode tick = addMethod(node, Opcodes.ACC_PUBLIC, "tickServer", "()Z");
        list(tick); tick.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0)); tick.instructions.add(new FieldInsnNode(Opcodes.GETFIELD, TARGET, "packetPosition", "I"));
        tick.instructions.add(new MethodInsnNode(Opcodes.INVOKESTATIC, "java/lang/Integer", "valueOf", "(I)Ljava/lang/Integer;", false)); tick.instructions.add(new MethodInsnNode(Opcodes.INVOKEINTERFACE, "java/util/List", "add", "(Ljava/lang/Object;)Z", true)); tick.instructions.add(new InsnNode(Opcodes.POP));
        list(tick); tick.instructions.add(new MethodInsnNode(Opcodes.INVOKEINTERFACE, "java/util/List", "isEmpty", "()Z", true)); tick.instructions.add(new InsnNode(Opcodes.IRETURN));
        MethodNode needs = addMethod(node, Opcodes.ACC_PUBLIC, "packetNeedsResponse", "(I)Z"); list(needs); needs.instructions.add(new VarInsnNode(Opcodes.ILOAD, 1)); needs.instructions.add(new MethodInsnNode(Opcodes.INVOKESTATIC, "java/lang/Integer", "valueOf", "(I)Ljava/lang/Integer;", false)); needs.instructions.add(new MethodInsnNode(Opcodes.INVOKEINTERFACE, "java/util/List", "contains", "(Ljava/lang/Object;)Z", true)); needs.instructions.add(new InsnNode(Opcodes.IRETURN));
        MethodNode untouched = addMethod(node, Opcodes.ACC_PUBLIC, "untouched", "()V"); untouched.instructions.add(new InsnNode(Opcodes.RETURN));
        return write(node, true);
    }
    private static void list(MethodNode method) { method.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0)); method.instructions.add(new FieldInsnNode(Opcodes.GETFIELD, TARGET, "sentMessages", LIST)); }
    private static InsnList initialize(String field) {
        InsnList insns = new InsnList(); insns.add(new VarInsnNode(Opcodes.ALOAD, 0)); insns.add(new TypeInsnNode(Opcodes.NEW, "java/util/ArrayList")); insns.add(new InsnNode(Opcodes.DUP)); insns.add(new MethodInsnNode(Opcodes.INVOKESPECIAL, "java/util/ArrayList", "<init>", "()V", false)); insns.add(new FieldInsnNode(Opcodes.PUTFIELD, TARGET, field, LIST)); return insns;
    }
    private static byte[] mutate(byte[] source, java.util.function.Consumer<ClassNode> action) { ClassNode node = read(source); action.accept(node); return write(node, false); }
    private static ClassNode read(byte[] bytes) { ClassNode node = new ClassNode(); new ClassReader(bytes).accept(node, 0); return node; }
    private static MethodNode method(ClassNode node, String name) { return node.methods.stream().filter(m -> m.name.equals(name)).findFirst().orElseThrow(); }
    private static MethodNode addMethod(ClassNode node, int access, String name, String desc) { MethodNode method = new MethodNode(access, name, desc, null, null); node.methods.add(method); return method; }
    private static byte[] write(ClassNode node, boolean frames) { ClassWriter writer = new ClassWriter(frames ? ClassWriter.COMPUTE_FRAMES | ClassWriter.COMPUTE_MAXS : ClassWriter.COMPUTE_MAXS); node.accept(writer); return writer.toByteArray(); }
}
