package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.*;

/** Adds transient Steam worlds to the ordinary multiplayer screen. */
public final class PortableSteamDiscoveryTransformer implements ClassFileTransformer {
    private static final String HOOKS = "minecraft/portable/identity/PortableSteamDiscoveryHooks";

    @Override
    public byte[] transform(Module module, ClassLoader loader, String name, Class<?> redefined,
                            ProtectionDomain domain, byte[] original) {
        if (!Boolean.getBoolean("minecraft.portable.identity.steamDiscoveryEnabled") ||
            !matches("steamDiscoveryScreenClasses", name)) return null;
        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(original).accept(node, 0);
        int initialized = 0, removed = 0, joined = 0;
        for (MethodNode method : node.methods) {
            if (matches("steamDiscoveryInitMethods", method.name) && method.desc.equals("()V")) {
                // As with LAN publishing, let vanilla and mod screen widgets
                // initialize before observing the finished screen.
                for (AbstractInsnNode instruction : method.instructions.toArray()) {
                    if (instruction.getOpcode() != Opcodes.RETURN) continue;
                    InsnList hook = new InsnList();
                    hook.add(new VarInsnNode(Opcodes.ALOAD, 0));
                    hook.add(call("begin", "(Ljava/lang/Object;)V"));
                    method.instructions.insertBefore(instruction, hook);
                    initialized++;
                }
            } else if (matches("steamDiscoveryRemovedMethods", method.name) && method.desc.equals("()V")) {
                InsnList hook = new InsnList();
                hook.add(new VarInsnNode(Opcodes.ALOAD, 0));
                hook.add(call("end", "(Ljava/lang/Object;)V"));
                method.instructions.insert(hook);
                removed++;
            } else if (matches("steamDiscoveryJoinMethods", method.name) &&
                       matches("steamDiscoveryJoinDescriptors", method.desc)) {
                InsnList hook = new InsnList();
                LabelNode vanilla = new LabelNode();
                hook.add(new VarInsnNode(Opcodes.ALOAD, 0));
                hook.add(new VarInsnNode(Opcodes.ALOAD, 1));
                hook.add(call("join", "(Ljava/lang/Object;Ljava/lang/Object;)Z"));
                hook.add(new JumpInsnNode(Opcodes.IFEQ, vanilla));
                hook.add(new InsnNode(Opcodes.RETURN));
                hook.add(vanilla);
                hook.add(new FrameNode(Opcodes.F_SAME, 0, null, 0, null));
                method.instructions.insert(hook);
                joined++;
            }
        }
        if (initialized == 0 || removed != 1 || joined != 1)
            throw new IllegalStateException("Unsupported multiplayer screen for Steam discovery.");
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched multiplayer Steam discovery.");
        return writer.toByteArray();
    }

    private static MethodInsnNode call(String name, String descriptor) {
        return new MethodInsnNode(Opcodes.INVOKESTATIC, HOOKS, name, descriptor, false);
    }

    private static boolean matches(String property, String value) {
        for (String candidate : System.getProperty("minecraft.portable.identity." + property, "").split(","))
            if (candidate.trim().equals(value)) return true;
        return false;
    }
}
