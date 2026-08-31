package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;

/**
 * Stops a guest being thrown backwards for a gap in his own connection.
 *
 * Two handlers are rewritten and nothing else: the one that takes a player's
 * position and the one that takes a vehicle's. In each, the game asks whether
 * this connection is the one that opened the world, and skips the
 * moved-too-quickly test if it is. That question is answered yes here.
 *
 * It is asked in four other places in the same class - who may change the
 * difficulty, and what to do when a connection ends among them - and those are
 * left alone, which is why the two methods are named rather than the class made
 * to answer differently everywhere. Patching the answer itself would hand a
 * guest the world's difficulty.
 */
public final class PortableGuestMovementTransformer implements ClassFileTransformer {
    private static final String HOOKS =
        "minecraft/portable/identity/PortableGuestMovementHooks";

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        if (!"true".equals(property("guestMovementEnabled", "false")) ||
            !contains(property("movementListenerClasses", ""), className)) {
            return null;
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);

        int patched = 0;
        for (MethodNode method : node.methods) {
            // Name and descriptor both: an obfuscated listener answers about
            // thirty packets and calls every one of those methods "a".
            if (matches(method, "handleMovePlayerMethods", "handleMovePlayerDescriptors") ||
                matches(method, "handleMoveVehicleMethods", "handleMoveVehicleDescriptors")) {
                patched += trustTheSender(method);
            }
        }
        if (patched == 0) {
            return null;
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        return writer.toByteArray();
    }

    /**
     * Every "am I the one who opened this world" inside the method becomes
     * "yes".
     *
     * One instruction for one instruction: the connection the question was
     * asked of is already on the stack and the hook takes it as its argument,
     * so the stack is left exactly as it was, no local is touched, no branch is
     * added and no frame changes.
     *
     * The owner is checked as well as the name, because the method moved onto a
     * shared parent class at 1.20.2 and both spellings are named.
     */
    private static int trustTheSender(MethodNode method) {
        String names = property("singleplayerOwnerMethods", "");
        String owners = property("singleplayerOwnerClasses", "");
        int replaced = 0;
        AbstractInsnNode instruction = method.instructions.getFirst();
        while (instruction != null) {
            AbstractInsnNode next = instruction.getNext();
            if (instruction instanceof MethodInsnNode call &&
                call.getOpcode() == Opcodes.INVOKEVIRTUAL &&
                call.desc.equals("()Z") &&
                contains(names, call.name) &&
                contains(owners, call.owner)) {
                method.instructions.set(instruction, new MethodInsnNode(
                    Opcodes.INVOKESTATIC,
                    HOOKS,
                    "trustedLikeTheHost",
                    "(Ljava/lang/Object;)Z",
                    false));
                replaced++;
            }
            instruction = next;
        }
        return replaced;
    }

    private static boolean matches(MethodNode method, String nameProperty, String descriptorProperty) {
        return contains(property(nameProperty, ""), method.name) &&
            contains(property(descriptorProperty, ""), method.desc);
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
