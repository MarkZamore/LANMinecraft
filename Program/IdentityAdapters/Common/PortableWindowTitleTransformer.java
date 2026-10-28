package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.InsnNode;
import org.objectweb.asm.tree.LdcInsnNode;
import org.objectweb.asm.tree.MethodNode;

/**
 * Uses the same seam as KubeJS's configured window title: the native producer.
 * Every normal window update keeps running, but reads this one stable string.
 * There are no window timers, game hooks or changes to connection/publication.
 */
public final class PortableWindowTitleTransformer implements ClassFileTransformer {
    private static final String PREFIX = "minecraft.portable.identity.";
    private static final String DESCRIPTOR = "()Ljava/lang/String;";

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        if (!Boolean.getBoolean(PREFIX + "windowTitleEnabled") ||
            !contains(System.getProperty(PREFIX + "windowTitleClasses", ""), className)) {
            return null;
        }

        String title = required("windowTitleText");
        String methods = required("createTitleMethods");
        String descriptor = System.getProperty(PREFIX + "createTitleDescriptors", DESCRIPTOR).trim();
        if (!DESCRIPTOR.equals(descriptor)) {
            throw unsupported("the configured producer descriptor is not " + DESCRIPTOR);
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);
        MethodNode target = null;
        for (MethodNode method : node.methods) {
            if (!contains(methods, method.name) || !DESCRIPTOR.equals(method.desc)) continue;
            if ((method.access & (Opcodes.ACC_STATIC | Opcodes.ACC_ABSTRACT | Opcodes.ACC_NATIVE)) != 0) {
                throw unsupported("the producer is not a concrete instance method");
            }
            if (target != null) throw unsupported("more than one producer matched");
            target = method;
        }
        if (target == null) throw unsupported("the configured String producer was not found");

        target.instructions.clear();
        target.tryCatchBlocks.clear();
        if (target.localVariables != null) target.localVariables.clear();
        target.visibleLocalVariableAnnotations = null;
        target.invisibleLocalVariableAnnotations = null;
        target.instructions.add(new LdcInsnNode(title));
        target.instructions.add(new InsnNode(Opcodes.ARETURN));
        target.maxStack = 1;
        target.maxLocals = 1;

        // No recomputation on unrelated methods. Their original frames and
        // max stack/locals remain intact; the replacement has no branches.
        ClassWriter writer = new ClassWriter(0);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched window title producer " + className + "." + target.name + ".");
        return writer.toByteArray();
    }

    private static String required(String name) {
        String value = System.getProperty(PREFIX + name);
        if (value == null || value.isBlank()) throw unsupported("missing " + name);
        return value;
    }

    private static boolean contains(String csv, String value) {
        if (value == null) return false;
        for (String candidate : csv.split(",")) {
            if (candidate.trim().replace('.', '/').equals(value)) return true;
        }
        return false;
    }

    private static IllegalStateException unsupported(String detail) {
        String message = "Unsupported Minecraft window title bytecode: " + detail + ".";
        System.err.println("[PortableIdentity] " + message);
        return new IllegalStateException(message);
    }
}
