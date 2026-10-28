package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.ConstantDynamic;
import org.objectweb.asm.Handle;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;
import org.objectweb.asm.tree.InvokeDynamicInsnNode;
import org.objectweb.asm.tree.LdcInsnNode;

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
            if (matches(method, "handleMovePlayerMethods", "handleMovePlayerDescriptors")) {
                int direct = trustTheSender(method);
                patched += direct;
                if (direct == 0) {
                    MethodNode helper = exclusiveMovementHelper(node, method);
                    if (helper != null) patched += trustTheSender(helper);
                }
            } else if (matches(method, "handleMoveVehicleMethods", "handleMoveVehicleDescriptors")) {
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
     * 26.2 delegates its on-foot test to shouldCheckPlayerMovement(boolean).
     * 26.3 adds a private position handler used by movement and teleport ACKs.
     * Only those exact mapped self-position paths may lead to the owner test.
     */
    private static MethodNode exclusiveMovementHelper(ClassNode owner, MethodNode player) {
        MethodNode selected = null;
        for (MethodNode candidate : owner.methods) {
            if ((candidate.access & Opcodes.ACC_PRIVATE) == 0 ||
                (candidate.access & Opcodes.ACC_STATIC) != 0 ||
                !candidate.desc.equals("(Z)Z") ||
                !matches(candidate, "movementCheckHelperMethods", "movementCheckHelperDescriptors") ||
                hasIndirectReference(owner, candidate)) continue;
            int calls = 0;
            boolean selectedCaller = true;
            for (MethodNode caller : owner.methods) {
                for (AbstractInsnNode instruction : caller.instructions) {
                    if (instruction instanceof MethodInsnNode call &&
                        call.owner.equals(owner.name) && call.name.equals(candidate.name) &&
                        call.desc.equals(candidate.desc)) {
                        calls++;
                        selectedCaller &= (caller == player || exclusivePositionCaller(owner, caller, player)) &&
                            (call.getOpcode() == Opcodes.INVOKEVIRTUAL || call.getOpcode() == Opcodes.INVOKESPECIAL);
                    }
                }
            }
            if (calls != 1 || !selectedCaller || ownerChecks(candidate) != 1) continue;
            if (selected != null) return null;
            selected = candidate;
        }
        return selected;
    }

    private static boolean exclusivePositionCaller(ClassNode owner, MethodNode position, MethodNode player) {
        if ((position.access & Opcodes.ACC_PRIVATE) == 0 ||
            (position.access & Opcodes.ACC_STATIC) != 0 || !position.desc.equals("(DDDFFZZ)V") ||
            !matches(position, "movementPositionMethods", "movementPositionDescriptors") ||
            hasIndirectReference(owner, position)) return false;
        int moves = 0;
        int acknowledgements = 0;
        for (MethodNode caller : owner.methods) {
            for (AbstractInsnNode instruction : caller.instructions) {
                if (!(instruction instanceof MethodInsnNode call) || !call.owner.equals(owner.name) ||
                    !call.name.equals(position.name) || !call.desc.equals(position.desc)) continue;
                if (call.getOpcode() != Opcodes.INVOKEVIRTUAL && call.getOpcode() != Opcodes.INVOKESPECIAL) return false;
                if (caller == player) moves++;
                else if ((caller.access & Opcodes.ACC_STATIC) == 0 &&
                    matches(caller, "movementTeleportAckMethods", "movementTeleportAckDescriptors")) acknowledgements++;
                else return false;
            }
        }
        return moves == 1 && acknowledgements == 1;
    }

    private static boolean hasIndirectReference(ClassNode owner, MethodNode target) {
        for (MethodNode caller : owner.methods) {
            for (AbstractInsnNode instruction : caller.instructions) {
                if (instruction instanceof LdcInsnNode constant && references(constant.cst, owner.name, target)) return true;
                if (instruction instanceof InvokeDynamicInsnNode dynamic) {
                    if (references(dynamic.bsm, owner.name, target)) return true;
                    for (Object argument : dynamic.bsmArgs) if (references(argument, owner.name, target)) return true;
                }
            }
        }
        return false;
    }

    private static boolean references(Object value, String owner, MethodNode target) {
        if (value instanceof Handle handle) return handle.getOwner().equals(owner) &&
            handle.getName().equals(target.name) && handle.getDesc().equals(target.desc);
        if (value instanceof ConstantDynamic dynamic) {
            if (references(dynamic.getBootstrapMethod(), owner, target)) return true;
            for (int index = 0; index < dynamic.getBootstrapMethodArgumentCount(); index++)
                if (references(dynamic.getBootstrapMethodArgument(index), owner, target)) return true;
        }
        return false;
    }

    private static int ownerChecks(MethodNode method) {
        int count = 0;
        for (AbstractInsnNode instruction : method.instructions) {
            if (instruction instanceof MethodInsnNode call && isOwnerCheck(call)) count++;
        }
        return count;
    }

    private static boolean isOwnerCheck(MethodInsnNode call) {
        return call.getOpcode() == Opcodes.INVOKEVIRTUAL && call.desc.equals("()Z") &&
            contains(property("singleplayerOwnerMethods", ""), call.name) &&
            contains(property("singleplayerOwnerClasses", ""), call.owner);
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
        int replaced = 0;
        AbstractInsnNode instruction = method.instructions.getFirst();
        while (instruction != null) {
            AbstractInsnNode next = instruction.getNext();
            if (instruction instanceof MethodInsnNode call && isOwnerCheck(call)) {
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
