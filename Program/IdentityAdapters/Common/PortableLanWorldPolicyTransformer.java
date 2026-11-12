package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.InsnList;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;
import org.objectweb.asm.tree.TypeInsnNode;
import org.objectweb.asm.tree.VarInsnNode;

public final class PortableLanWorldPolicyTransformer implements ClassFileTransformer {
    private static final String PREFIX = "minecraft.portable.identity.";
    private static final String HOOKS = "minecraft/portable/identity/PortableLanWorldPolicyHooks";

    @Override
    public byte[] transform(Module module, ClassLoader loader, String className, Class<?> classBeingRedefined,
                            ProtectionDomain protectionDomain, byte[] original) {
        if (!isTarget(className)) return null;
        boolean integrated = matches("integratedServerClasses", className);
        ClassNode node = read(original);
        MethodNode target = targetMethod(node, integrated);
        target.instructions.insert(guard(integrated, target.desc));

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        return writer.toByteArray();
    }

    static boolean isTarget(String className) {
        return Boolean.getBoolean(PREFIX + "lanPreserveWorldSettingsEnabled") &&
            (matches("integratedServerClasses", className) || matches("playerListClasses", className));
    }

    static void verify(byte[] original, byte[] transformed, String className) {
        boolean integrated = matches("integratedServerClasses", className);
        MethodNode before = targetMethod(read(original), integrated);
        MethodNode after = targetMethod(read(transformed), integrated);
        InsnList expected = guard(integrated, before.desc);
        if (!before.name.equals(after.name) || !before.desc.equals(after.desc) ||
            hookCount(before) != 0 || hookCount(after) != (integrated ? 2 : 1)) {
            throw unsupported(className, "unexpected policy call count or publication method");
        }

        AbstractInsnNode current = after.instructions.getFirst();
        for (AbstractInsnNode instruction : expected) {
            while (current != null && current.getOpcode() < 0) current = current.getNext();
            if (!sameInstruction(instruction, current)) throw unsupported(className, "missing argument guard");
            current = current.getNext();
        }
    }

    private static ClassNode read(byte[] bytes) {
        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(bytes).accept(node, 0);
        return node;
    }

    private static MethodNode targetMethod(ClassNode node, boolean integrated) {
        String methods = integrated ? "publishServerMethods" : "lanPolicySetAllowCommandsMethods";
        MethodNode target = null;
        for (MethodNode method : node.methods) {
            if ((method.access & (Opcodes.ACC_ABSTRACT | Opcodes.ACC_STATIC | Opcodes.ACC_NATIVE)) != 0 ||
                !matches(methods, method.name) || !validDescriptor(integrated, method.desc)) continue;
            if (target != null) throw unsupported(node.name, "ambiguous " + methods);
            target = method;
        }
        if (target == null) throw unsupported(node.name, "missing " + methods);
        return target;
    }

    private static InsnList guard(boolean integrated, String descriptor) {
        InsnList guard = new InsnList();
        if (integrated) {
            guard.add(new VarInsnNode(Opcodes.ALOAD, 0));
            guard.add(new VarInsnNode(Opcodes.ALOAD, 1));
            guard.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HOOKS, "publicationGameType",
                "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;", false));
            guard.add(new TypeInsnNode(Opcodes.CHECKCAST, Type.getArgumentTypes(descriptor)[0].getInternalName()));
            guard.add(new VarInsnNode(Opcodes.ASTORE, 1));
        }
        int permissionSlot = integrated ? 2 : 1;
        guard.add(new VarInsnNode(Opcodes.ALOAD, 0));
        guard.add(new VarInsnNode(Opcodes.ILOAD, permissionSlot));
        guard.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HOOKS,
            integrated ? "publicationAllowsCommands" : "playerListAllowsCommands", "(Ljava/lang/Object;Z)Z", false));
        guard.add(new VarInsnNode(Opcodes.ISTORE, permissionSlot));
        return guard;
    }

    private static int hookCount(MethodNode method) {
        int count = 0;
        for (AbstractInsnNode instruction : method.instructions) {
            if (instruction instanceof MethodInsnNode call && call.owner.equals(HOOKS)) count++;
        }
        return count;
    }

    private static boolean sameInstruction(AbstractInsnNode expected, AbstractInsnNode actual) {
        if (actual == null || actual.getOpcode() != expected.getOpcode()) return false;
        if (expected instanceof VarInsnNode variable && actual instanceof VarInsnNode found) return variable.var == found.var;
        if (expected instanceof TypeInsnNode type && actual instanceof TypeInsnNode found) return type.desc.equals(found.desc);
        if (expected instanceof MethodInsnNode call && actual instanceof MethodInsnNode found) {
            return !found.itf && call.owner.equals(found.owner) && call.name.equals(found.name) && call.desc.equals(found.desc);
        }
        return false;
    }

    private static boolean validDescriptor(boolean integrated, String descriptor) {
        if (!integrated) return descriptor.equals("(Z)V");
        return descriptor.startsWith("(L") && descriptor.endsWith(";ZI)Z") &&
            matches("publishServerDescriptors", descriptor);
    }

    private static boolean matches(String property, String value) {
        if (value == null || value.isEmpty()) return false;
        for (String alias : System.getProperty(PREFIX + property, "").split(",")) {
            if (value.equals(alias)) return true;
        }
        return false;
    }

    private static IllegalStateException unsupported(String target, String reason) {
        return new IllegalStateException("Cannot enforce the saved LAN world settings in " + target + ": " + reason);
    }
}
