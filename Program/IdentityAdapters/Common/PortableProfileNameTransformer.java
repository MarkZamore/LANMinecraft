package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.FrameNode;
import org.objectweb.asm.tree.InsnList;
import org.objectweb.asm.tree.InsnNode;
import org.objectweb.asm.tree.JumpInsnNode;
import org.objectweb.asm.tree.LabelNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;
import org.objectweb.asm.tree.VarInsnNode;

/** Supplies portable names to optional tooltip/name caches on every loader. */
public final class PortableProfileNameTransformer implements ClassFileTransformer {
    public static boolean isTarget(String name) {
        return "snownee/jade/util/CommonProxy".equals(name)
            || "net/neoforged/neoforge/common/UsernameCache".equals(name)
            || "net/minecraftforge/common/UsernameCache".equals(name);
    }

    @Override
    public byte[] transform(Module module, ClassLoader loader, String className,
        Class<?> classBeingRedefined, ProtectionDomain protectionDomain, byte[] original) {
        if (!isTarget(className)) {
            return null;
        }
        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(original).accept(node, 0);
        boolean patched = false;
        for (MethodNode method : node.methods) {
            if (!method.name.equals("getLastKnownUsername")
                || !method.desc.equals("(Ljava/util/UUID;)Ljava/lang/String;")
                || (method.access & Opcodes.ACC_STATIC) == 0
                || (method.access & (Opcodes.ACC_ABSTRACT | Opcodes.ACC_NATIVE)) != 0) {
                continue;
            }
            // Jade asks Mojang and the loader's cache, neither of which knows
            // a Steam-derived UUID. The launcher already knows its exact name.
            // Unknown UUIDs keep the original lookup, including its null result.
            LabelNode fallback = new LabelNode();
            InsnList prefix = new InsnList();
            prefix.add(new VarInsnNode(Opcodes.ALOAD, 0));
            prefix.add(new MethodInsnNode(Opcodes.INVOKESTATIC,
                "minecraft/portable/identity/PortableIdentityProfiles", "nameOf",
                "(Ljava/util/UUID;)Ljava/lang/String;", false));
            prefix.add(new InsnNode(Opcodes.DUP));
            prefix.add(new JumpInsnNode(Opcodes.IFNULL, fallback));
            prefix.add(new InsnNode(Opcodes.ARETURN));
            prefix.add(fallback);
            prefix.add(new FrameNode(Opcodes.F_SAME1, 0, null, 1,
                new Object[] { "java/lang/String" }));
            prefix.add(new InsnNode(Opcodes.POP));
            method.instructions.insert(prefix);
            patched = true;
        }
        // Optional integrations must not prevent a different mod version loading.
        if (!patched) {
            System.err.println("[PortableIdentity] Name lookup not patched: " + className
                + " has no supported getLastKnownUsername(UUID) method.");
            return null;
        }
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Portable player names enabled for " + className + ".");
        return writer.toByteArray();
    }
}
