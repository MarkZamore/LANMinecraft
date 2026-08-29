package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.FrameNode;
import org.objectweb.asm.tree.InsnList;
import org.objectweb.asm.tree.InsnNode;
import org.objectweb.asm.tree.JumpInsnNode;
import org.objectweb.asm.tree.LabelNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;
import org.objectweb.asm.tree.TypeInsnNode;
import org.objectweb.asm.tree.VarInsnNode;

/**
 * Gives the launcher's own skins the moment they need to arrive in.
 *
 * <p>The patched method is SkinManager.getInsecureSkin, which answers with
 * whatever the loader has this instant. A prefix asks the hook first: for a
 * profile the launcher serves a skin for, and once per profile, the hook waits
 * a fraction of a second and hands back the real face. Anything else - another
 * player, a second call, a timeout, a failure of any kind - returns null and
 * the original body runs untouched.
 */
public final class PortableSkinWaitTransformer implements ClassFileTransformer {
    private static final String HOOKS =
        "minecraft/portable/identity/PortableSkinWaitHooks";
    private static final String PROFILE_DESCRIPTOR = "(Lcom/mojang/authlib/GameProfile;)";

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        if (!Boolean.getBoolean("minecraft.portable.identity.skinWaitEnabled") ||
            !contains(property("skinManagerClasses", ""), className)) {
            return null;
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);

        boolean patched = false;
        String wanted = property("insecureSkinMethods", "getInsecureSkin,b");
        for (MethodNode method : node.methods) {
            if (!contains(wanted, method.name) ||
                !method.desc.startsWith(PROFILE_DESCRIPTOR) ||
                Type.getReturnType(method.desc).getSort() != Type.OBJECT) {
                continue;
            }

            // The class of the skin is read off the method's own signature, so
            // nothing here has to know its name in either mapping.
            String skinType = Type.getReturnType(method.desc).getInternalName();
            LabelNode vanilla = new LabelNode();
            InsnList prefix = new InsnList();
            prefix.add(new VarInsnNode(Opcodes.ALOAD, 0));
            prefix.add(new VarInsnNode(Opcodes.ALOAD, 1));
            prefix.add(new MethodInsnNode(
                Opcodes.INVOKESTATIC,
                HOOKS,
                "awaitSkin",
                "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;",
                false));
            prefix.add(new InsnNode(Opcodes.DUP));
            prefix.add(new JumpInsnNode(Opcodes.IFNULL, vanilla));
            prefix.add(new TypeInsnNode(Opcodes.CHECKCAST, skinType));
            prefix.add(new InsnNode(Opcodes.ARETURN));
            prefix.add(vanilla);
            // COMPUTE_MAXS does not compute frames, so the one the jump lands
            // on is written by hand. The null is still on the stack there.
            prefix.add(new FrameNode(
                Opcodes.F_SAME1, 0, null, 1, new Object[] { "java/lang/Object" }));
            prefix.add(new InsnNode(Opcodes.POP));
            method.instructions.insert(prefix);
            patched = true;
        }

        if (!patched) {
            // The JVM swallows a transformer's exception, so without this line
            // the default face would come back with no explanation.
            System.err.println(
                "[PortableIdentity] Skin wait patch failed for " + className + ".");
            throw new IllegalStateException(
                "Unsupported Minecraft skin manager bytecode: getInsecureSkin was not found.");
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched skin manager class " + className + ".");
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
