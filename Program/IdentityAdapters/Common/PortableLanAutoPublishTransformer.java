package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import jdk.internal.org.objectweb.asm.ClassReader;
import jdk.internal.org.objectweb.asm.ClassWriter;
import jdk.internal.org.objectweb.asm.Opcodes;
import jdk.internal.org.objectweb.asm.tree.ClassNode;
import jdk.internal.org.objectweb.asm.tree.FrameNode;
import jdk.internal.org.objectweb.asm.tree.InsnList;
import jdk.internal.org.objectweb.asm.tree.InsnNode;
import jdk.internal.org.objectweb.asm.tree.JumpInsnNode;
import jdk.internal.org.objectweb.asm.tree.LabelNode;
import jdk.internal.org.objectweb.asm.tree.MethodInsnNode;
import jdk.internal.org.objectweb.asm.tree.MethodNode;
import jdk.internal.org.objectweb.asm.tree.VarInsnNode;

public final class PortableLanAutoPublishTransformer implements ClassFileTransformer {
    private static final String HOOKS =
        "minecraft/portable/identity/PortableLanAutoPublishHooks";

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        if (!contains(property("lanShareScreenClasses", ""), className)) {
            return null;
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);

        boolean patched = false;
        String initMethods = property("lanShareInitMethods", "init,aT_");
        for (MethodNode method : node.methods) {
            if (!contains(initMethods, method.name) || !method.desc.equals("()V")) {
                continue;
            }

            LabelNode continueLabel = new LabelNode();
            InsnList prefix = new InsnList();
            prefix.add(new VarInsnNode(Opcodes.ALOAD, 0));
            prefix.add(new MethodInsnNode(
                Opcodes.INVOKESTATIC,
                HOOKS,
                "autoPublish",
                "(Ljava/lang/Object;)Z",
                false));
            prefix.add(new JumpInsnNode(Opcodes.IFEQ, continueLabel));
            prefix.add(new InsnNode(Opcodes.RETURN));
            prefix.add(continueLabel);
            prefix.add(new FrameNode(Opcodes.F_SAME, 0, null, 0, null));
            method.instructions.insert(prefix);
            patched = true;
        }

        if (!patched) {
            throw unsupported("share screen init was not found");
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched LAN share screen class " + className + ".");
        return writer.toByteArray();
    }

    private static String property(String name, String fallback) {
        String value = System.getProperty("minecraft.portable.identity." + name);
        return value == null || value.isBlank() ? fallback : value;
    }

    private static boolean contains(String csv, String value) {
        for (String candidate : csv.split(",")) {
            if (candidate.trim().equals(value)) {
                return true;
            }
        }
        return false;
    }

    private static IllegalStateException unsupported(String detail) {
        return new IllegalStateException("Unsupported Minecraft LAN share screen bytecode: " + detail + ".");
    }
}
