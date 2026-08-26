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

public final class PortableFtbTeleportTransformer implements ClassFileTransformer {
    private static final String DEFAULT_TARGETS =
        "dev/ftb/mods/ftbchunks/client/gui/WaypointEditorScreen$RowPanel," +
        "dev/ftb/mods/ftbchunks/client/mapicon/WaypointMapIcon," +
        "dev/ftb/mods/ftbchunks/net/TeleportFromMapPacket";

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        if (!Boolean.getBoolean("minecraft.portable.identity.ftbTeleportEnabled") ||
            !contains(property("ftbTeleportClasses", DEFAULT_TARGETS), className)) {
            return null;
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);

        // The launcher's teleport commands already let every player move
        // themselves anywhere, so FTB Chunks' operator gate on the equivalent
        // map teleport adds no protection and only hides the button.
        int patched = 0;
        String permissionMethods = property("ftbPermissionMethods", "hasPermissions");
        for (MethodNode method : node.methods) {
            java.util.List<MethodInsnNode> calls = new java.util.ArrayList<>();
            for (AbstractInsnNode instruction = method.instructions.getFirst();
                 instruction != null;
                 instruction = instruction.getNext()) {
                if (!(instruction instanceof MethodInsnNode)) {
                    continue;
                }
                MethodInsnNode call = (MethodInsnNode) instruction;
                if (call.getOpcode() == Opcodes.INVOKEVIRTUAL &&
                    contains(permissionMethods, call.name) &&
                    call.desc.equals("(I)Z")) {
                    calls.add(call);
                }
            }

            for (MethodInsnNode call : calls) {
                InsnList replacement = new InsnList();
                replacement.add(new InsnNode(Opcodes.POP));
                replacement.add(new InsnNode(Opcodes.POP));
                replacement.add(new InsnNode(Opcodes.ICONST_1));
                method.instructions.insertBefore(call, replacement);
                method.instructions.remove(call);
                patched++;
            }
        }

        if (patched != 1) {
            // The JVM swallows transformer failures, so without this line a
            // mixin-shifted permission check would silently restore the
            // operator-only teleport button.
            System.err.println(
                "[PortableIdentity] FTB Chunks teleport patch failed for " + className +
                ": found " + patched + " permission checks.");
            throw new IllegalStateException(
                "Unsupported FTB Chunks teleport bytecode: expected one permission check in " +
                className + ", found " + patched + ".");
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched FTB Chunks teleport class " + className + ".");
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
}
