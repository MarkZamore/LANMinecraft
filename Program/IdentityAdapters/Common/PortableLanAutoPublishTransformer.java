package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.InsnList;
import org.objectweb.asm.tree.InsnNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;
import org.objectweb.asm.tree.VarInsnNode;

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
        // The class list is named even when the hook is off, because the
        // preflight asks for it by name before it looks at anything else. What
        // decides whether anything is patched is this flag: a Minecraft missing
        // one of the names the hook reaches for keeps its skins and its UUID
        // and simply does not get the one-press publish.
        if (!"true".equals(property("lanPublishEnabled", "false"))) {
            return null;
        }
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

            // The screen builds itself first and the publish comes after it,
            // rather than instead of it. Mods are handed the finished screen
            // the moment init returns and they read the buttons it made: LAN
            // Server Properties looks for the start button by name and dies on
            // the null when it is not there, which is what a skipped body left
            // behind - the world opened and the game came down with it.
            AbstractInsnNode exit = method.instructions.getLast();
            while (exit != null && exit.getOpcode() != Opcodes.RETURN) {
                exit = exit.getPrevious();
            }
            if (exit == null) {
                continue;
            }

            InsnList publish = new InsnList();
            publish.add(new VarInsnNode(Opcodes.ALOAD, 0));
            publish.add(new MethodInsnNode(
                Opcodes.INVOKESTATIC,
                HOOKS,
                "autoPublish",
                "(Ljava/lang/Object;)Z",
                false));
            publish.add(new InsnNode(Opcodes.POP));
            method.instructions.insertBefore(exit, publish);
            patched = true;
        }

        if (!patched) {
            // The JVM swallows transformer failures, so without this line the
            // vanilla settings screen would come back with no explanation.
            System.err.println(
                "[PortableIdentity] LAN share screen patch failed for " + className + ".");
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
