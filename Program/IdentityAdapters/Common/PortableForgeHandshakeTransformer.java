package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import java.util.Arrays;
import java.util.HashSet;
import java.util.Set;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;
import org.objectweb.asm.Handle;
import org.objectweb.asm.ConstantDynamic;
import org.objectweb.asm.tree.*;

/** Serializes only the Forge login acknowledgement list, never the handshake callbacks. */
public final class PortableForgeHandshakeTransformer implements ClassFileTransformer {
    public static final String TARGET = "net/minecraftforge/network/HandshakeHandler";
    private static final String FIELD = "sentMessages";
    private static final String LIST = "Ljava/util/List;";
    private static final String WRAP = "(Ljava/util/List;)Ljava/util/List;";
    private static final Set<String> OPERATIONS = Set.of(
        "add(Ljava/lang/Object;)Z", "removeIf(Ljava/util/function/Predicate;)Z",
        "isEmpty()Z", "contains(Ljava/lang/Object;)Z");

    @Override
    public byte[] transform(Module module, ClassLoader loader, String name,
            Class<?> redefined, ProtectionDomain domain, byte[] bytes) {
        if (!TARGET.equals(name) ||
                !Boolean.getBoolean("minecraft.portable.identity.forgeHandshakeSyncEnabled")) return null;
        ClassNode node = read(bytes);
        FieldInsnNode store = inspect(node);
        if (isWrapper(previous(store))) return null;
        MethodNode constructor = constructorContaining(node, store);
        constructor.instructions.insertBefore(store, new MethodInsnNode(Opcodes.INVOKESTATIC,
            "java/util/Collections", "synchronizedList", WRAP, false));
        byte[] result = write(node);
        verify(bytes, result);
        System.out.println("[PortableIdentity] Serialized Forge handshake acknowledgements.");
        return result;
    }

    /** Refuse a changed field contract or any escaping/iterating use of the list. */
    private static FieldInsnNode inspect(ClassNode node) {
        if (!TARGET.equals(node.name)) throw unsupported("wrong owner");
        long fields = node.fields.stream().filter(f -> f.name.equals(FIELD) && f.desc.equals(LIST) &&
            (f.access & (Opcodes.ACC_PRIVATE | Opcodes.ACC_STATIC)) == Opcodes.ACC_PRIVATE).count();
        if (fields != 1) throw unsupported("expected one private instance List");
        FieldInsnNode store = null;
        Set<String> operations = new HashSet<>();
        int reads = 0;
        for (MethodNode method : node.methods) {
            for (AbstractInsnNode instruction : method.instructions) {
                if (instruction instanceof LdcInsnNode constant && fieldHandle(constant.cst))
                    throw unsupported("field escapes through a constant handle");
                if (instruction instanceof InvokeDynamicInsnNode dynamic)
                    for (Object argument : dynamic.bsmArgs)
                        if (fieldHandle(argument)) throw unsupported("field escapes through a bootstrap handle");
                if (!(instruction instanceof FieldInsnNode field) || !field.owner.equals(TARGET) ||
                        !field.name.equals(FIELD)) continue;
                if (!field.desc.equals(LIST)) throw unsupported("changed field type");
                if (field.getOpcode() == Opcodes.PUTFIELD) {
                    if (store != null || !method.name.equals("<init>"))
                        throw unsupported("ambiguous or reassigned list");
                    AbstractInsnNode init = previous(field);
                    if (isWrapper(init)) init = previous(init);
                    if (!(init instanceof MethodInsnNode call) || call.getOpcode() != Opcodes.INVOKESPECIAL ||
                            !call.owner.equals("java/util/ArrayList") || !call.name.equals("<init>") ||
                            !call.desc.equals("()V") || previous(init).getOpcode() != Opcodes.DUP ||
                            !(previous(previous(init)) instanceof TypeInsnNode allocation) ||
                            allocation.getOpcode() != Opcodes.NEW || !allocation.desc.equals("java/util/ArrayList"))
                        throw unsupported("unreviewed initializer");
                    store = field;
                } else if (field.getOpcode() == Opcodes.GETFIELD) {
                    reads++;
                    String operation = consumer(node, field);
                    if (!operations.add(operation)) throw unsupported("duplicate list consumer");
                } else throw unsupported("unreviewed field access");
            }
        }
        if (store == null || reads != 4 || !operations.equals(OPERATIONS))
            throw unsupported("unreviewed acknowledgement operations");
        return store;
    }

    private static String consumer(ClassNode owner, FieldInsnNode field) {
        AbstractInsnNode cursor = field;
        for (int count = 0; count < 8; count++) {
            cursor = next(cursor);
            if (cursor instanceof MethodInsnNode call) {
                String signature = call.name + call.desc;
                if (call.getOpcode() == Opcodes.INVOKEINTERFACE && call.owner.equals("java/util/List") &&
                        OPERATIONS.contains(signature)) return signature;
                if (call.getOpcode() == Opcodes.INVOKESTATIC && call.owner.equals("java/lang/Integer") &&
                        call.name.equals("valueOf") && call.desc.equals("(I)Ljava/lang/Integer;")) continue;
                throw unsupported("list escapes through a method call");
            }
            if (cursor instanceof VarInsnNode load &&
                    (load.getOpcode() == Opcodes.ALOAD || load.getOpcode() == Opcodes.ILOAD)) continue;
            if (cursor instanceof FieldInsnNode primitive && primitive.getOpcode() == Opcodes.GETFIELD &&
                    primitive.owner.equals(TARGET) && primitive.desc.equals("I")) continue;
            if (cursor instanceof InvokeDynamicInsnNode lambda) {
                verifyPredicate(owner, lambda);
                continue;
            }
            throw unsupported("list escapes or uses an unsupported operation");
        }
        throw unsupported("list consumer not found");
    }

    private static boolean fieldHandle(Object value) {
        if (value instanceof Handle handle)
            return handle.getOwner().equals(TARGET) && handle.getName().equals(FIELD);
        if (value instanceof ConstantDynamic constant)
            for (int i = 0; i < constant.getBootstrapMethodArgumentCount(); i++)
                if (fieldHandle(constant.getBootstrapMethodArgument(i))) return true;
        return false;
    }

    private static void verifyPredicate(ClassNode owner, InvokeDynamicInsnNode lambda) {
        if (!lambda.desc.equals("(Ljava/util/function/IntSupplier;)Ljava/util/function/Predicate;") ||
                lambda.bsm.getTag() != Opcodes.H_INVOKESTATIC ||
                !lambda.bsm.getOwner().equals("java/lang/invoke/LambdaMetafactory") ||
                !lambda.bsm.getName().equals("metafactory") || lambda.bsmArgs.length != 3 ||
                !(lambda.bsmArgs[0] instanceof Type erased) || !erased.getDescriptor().equals("(Ljava/lang/Object;)Z") ||
                !(lambda.bsmArgs[1] instanceof Handle impl) || impl.getTag() != Opcodes.H_INVOKESTATIC ||
                !impl.getOwner().equals(TARGET) ||
                !impl.getDesc().equals("(Ljava/util/function/IntSupplier;Ljava/lang/Integer;)Z") ||
                !(lambda.bsmArgs[2] instanceof Type typed) || !typed.getDescriptor().equals("(Ljava/lang/Integer;)Z"))
            throw unsupported("unreviewed ACK predicate capture");
        MethodNode predicate = owner.methods.stream().filter(method -> method.name.equals(impl.getName()) &&
            method.desc.equals(impl.getDesc()) &&
            (method.access & (Opcodes.ACC_PRIVATE | Opcodes.ACC_STATIC)) == (Opcodes.ACC_PRIVATE | Opcodes.ACC_STATIC))
            .findFirst().orElseThrow(() -> unsupported("ACK predicate implementation missing"));
        Set<String> calls = new HashSet<>();
        for (AbstractInsnNode instruction : predicate.instructions) {
            int op = instruction.getOpcode();
            if (op < 0 || op == Opcodes.ALOAD || op == Opcodes.ICONST_0 || op == Opcodes.ICONST_1 ||
                    op == Opcodes.IF_ICMPEQ || op == Opcodes.IF_ICMPNE || op == Opcodes.GOTO || op == Opcodes.IRETURN) continue;
            if (instruction instanceof MethodInsnNode call) {
                String signature = call.owner + "." + call.name + call.desc;
                if ((call.getOpcode() == Opcodes.INVOKEVIRTUAL && signature.equals("java/lang/Integer.intValue()I") ||
                     call.getOpcode() == Opcodes.INVOKEINTERFACE && signature.equals("java/util/function/IntSupplier.getAsInt()I")) &&
                        calls.add(signature)) continue;
            }
            throw unsupported("ACK predicate does more than compare indices");
        }
        if (calls.size() != 2) throw unsupported("ACK predicate lacks index comparison");
    }

    /** Removing the one wrapper must reconstruct the complete original class. */
    public static void verify(byte[] original, byte[] transformed) {
        ClassNode before = read(original), after = read(transformed);
        FieldInsnNode source = inspect(before), target = inspect(after);
        if (isWrapper(previous(source)) || !isWrapper(previous(target)))
            throw unsupported("expected exactly one new list wrapper");
        constructorContaining(after, target).instructions.remove(previous(target));
        if (!Arrays.equals(write(before), write(after)))
            throw unsupported("changes outside the acknowledgement initializer");
    }

    private static MethodNode constructorContaining(ClassNode node, AbstractInsnNode instruction) {
        for (MethodNode method : node.methods)
            if (method.instructions.indexOf(instruction) >= 0) return method;
        throw unsupported("initializer is not in the class");
    }
    private static boolean isWrapper(AbstractInsnNode node) {
        return node instanceof MethodInsnNode call && call.getOpcode() == Opcodes.INVOKESTATIC &&
            call.owner.equals("java/util/Collections") && call.name.equals("synchronizedList") && call.desc.equals(WRAP);
    }
    private static AbstractInsnNode previous(AbstractInsnNode node) {
        if (node == null) throw unsupported("truncated initializer");
        do { node = node.getPrevious(); } while (node != null && node.getOpcode() < 0);
        if (node == null) throw unsupported("truncated initializer");
        return node;
    }
    private static AbstractInsnNode next(AbstractInsnNode node) {
        if (node == null) throw unsupported("truncated consumer");
        do { node = node.getNext(); } while (node != null && node.getOpcode() < 0);
        if (node == null) throw unsupported("truncated consumer");
        return node;
    }
    private static IllegalStateException unsupported(String reason) {
        return new IllegalStateException("Unsupported Forge handshake bytecode: " + reason);
    }
    private static ClassNode read(byte[] bytes) {
        ClassNode node = new ClassNode(Opcodes.ASM9); new ClassReader(bytes).accept(node, 0); return node;
    }
    private static byte[] write(ClassNode node) {
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS); node.accept(writer); return writer.toByteArray();
    }
}
