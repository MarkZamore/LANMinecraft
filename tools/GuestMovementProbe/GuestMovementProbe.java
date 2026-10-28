import java.lang.reflect.InvocationTargetException;
import java.util.Arrays;
import minecraft.portable.identity.PortableGuestMovementTransformer;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Handle;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.InsnNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;
import org.objectweb.asm.tree.VarInsnNode;
import org.objectweb.asm.tree.LdcInsnNode;

/** Executable bytecode regression for direct and delegated movement guards. */
public final class GuestMovementProbe {
    private static final String OWNER = "probe/Movement";
    private static final String HOOKS = "minecraft/portable/identity/PortableGuestMovementHooks";

    public static void main(String[] args) throws Exception {
        property("guestMovementEnabled", "true");
        property("movementListenerClasses", OWNER);
        property("handleMovePlayerMethods", "player");
        property("handleMoveVehicleMethods", "vehicle");
        property("handleMovePlayerDescriptors", "(Ljava/lang/Object;)V");
        property("handleMoveVehicleDescriptors", "(Ljava/lang/Object;)V");
        property("singleplayerOwnerMethods", "isOwner");
        property("singleplayerOwnerClasses", OWNER);
        property("movementCheckHelperMethods", "shouldCheck");
        property("movementCheckHelperDescriptors", "(Z)Z");
        property("movementPositionMethods", "position");
        property("movementPositionDescriptors", "(DDDFFZZ)V");
        property("movementTeleportAckMethods", "ack");
        property("movementTeleportAckDescriptors", "(Ljava/lang/Object;)V");

        check("legacy direct guards", fixture(false, false, true, "shouldCheck", 1), true, "player");
        check("26.2 exclusive private helper", fixture(true, false, true, "shouldCheck", 1), true, "shouldCheck");
        check("helper shared with permission handler", fixture(true, true, true, "shouldCheck", 1), false, null);
        check("public helper", fixture(true, false, false, "shouldCheck", 1), false, null);
        check("unmapped private helper with same signature", fixture(true, false, true, "otherCheck", 1), false, null);
        check("ambiguous owner guards in helper", fixture(true, false, true, "shouldCheck", 2), false, null);
        check("26.3 private position plus teleport ACK", positionFixture(false, false, false, false), true, "shouldCheck");
        check("position helper shared with foreign handler", positionFixture(true, false, false, false), false, null);
        check("position helper escapes as method handle", positionFixture(false, true, false, false), false, null);
        check("owner-test helper escapes as method handle", positionFixture(false, false, true, false), false, null);
        check("teleport ACK has unconfigured descriptor", positionFixture(false, false, false, true), false, null);

        // Two total hooks in the wrong methods must never satisfy preflight.
        byte[] source = fixture(false, false, true, "shouldCheck", 1);
        ClassNode wrong = read(source);
        for (MethodNode method : wrong.methods) {
            if (!method.name.equals("vehicle") && !method.name.equals("permission")) continue;
            for (var instruction : method.instructions.toArray()) {
                if (instruction instanceof MethodInsnNode call && call.name.equals("isOwner"))
                    method.instructions.set(call, hook());
            }
        }
        if (accepted(source, write(wrong))) throw new AssertionError("Preflight accepted permission escalation.");
        System.out.println("PASS guest movement: 3 supported layouts, 9 rejected unsafe layouts; unrelated permission guard preserved.");
    }

    private static void check(String label, byte[] source, boolean expected, String playerSite) throws Exception {
        byte[] transformed = new PortableGuestMovementTransformer().transform(
            null, GuestMovementProbe.class.getClassLoader(), OWNER, null, null, source);
        if (transformed == null) throw new AssertionError(label + ": transformer returned no vehicle patch");
        if (accepted(source, transformed) != expected) throw new AssertionError(label + ": unexpected preflight verdict");
        ClassNode patched = read(transformed);
        for (MethodNode method : patched.methods) {
            long hooks = Arrays.stream(method.instructions.toArray()).filter(instruction ->
                instruction instanceof MethodInsnNode call && call.owner.equals(HOOKS)).count();
            if (method.name.equals("permission") && hooks != 0)
                throw new AssertionError(label + ": permission guard was patched");
            if (expected && method.name.equals(playerSite) && hooks != 1)
                throw new AssertionError(label + ": on-foot check was missed");
        }
    }

    private static boolean accepted(byte[] source, byte[] transformed) throws Exception {
        var type = Class.forName("minecraft.portable.identity.PortableIdentityPreflight");
        var verify = type.getDeclaredMethod("verifyGuestMovementTargets", String.class, byte[].class, byte[].class);
        verify.setAccessible(true);
        try { verify.invoke(null, OWNER, source, transformed); return true; }
        catch (InvocationTargetException rejected) {
            if (rejected.getCause() instanceof IllegalStateException) return false;
            throw rejected;
        }
    }

    private static byte[] fixture(boolean delegated, boolean shared, boolean privateHelper, String helperName, int guards) {
        ClassNode node = new ClassNode();
        node.version = Opcodes.V17; node.access = Opcodes.ACC_PUBLIC; node.name = OWNER; node.superName = "java/lang/Object";
        MethodNode player = method(node, Opcodes.ACC_PUBLIC, "player", "(Ljava/lang/Object;)V");
        if (delegated) {
            player.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0));
            player.instructions.add(new InsnNode(Opcodes.ICONST_0));
            player.instructions.add(new MethodInsnNode(Opcodes.INVOKEVIRTUAL, OWNER, helperName, "(Z)Z", false));
        } else ownerGuard(player);
        player.instructions.add(new InsnNode(Opcodes.POP)); player.instructions.add(new InsnNode(Opcodes.RETURN));
        MethodNode vehicle = method(node, Opcodes.ACC_PUBLIC, "vehicle", "(Ljava/lang/Object;)V");
        ownerGuard(vehicle); vehicle.instructions.add(new InsnNode(Opcodes.POP)); vehicle.instructions.add(new InsnNode(Opcodes.RETURN));
        MethodNode helper = method(node, privateHelper ? Opcodes.ACC_PRIVATE : Opcodes.ACC_PUBLIC, helperName, "(Z)Z");
        for (int index = 0; index < guards; index++) { ownerGuard(helper); helper.instructions.add(new InsnNode(Opcodes.POP)); }
        helper.instructions.add(new InsnNode(Opcodes.ICONST_1)); helper.instructions.add(new InsnNode(Opcodes.IRETURN));
        MethodNode permission = method(node, Opcodes.ACC_PRIVATE, "permission", "(Z)Z");
        ownerGuard(permission); permission.instructions.add(new InsnNode(Opcodes.POP));
        if (shared) {
            permission.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0));
            permission.instructions.add(new InsnNode(Opcodes.ICONST_0));
            permission.instructions.add(new MethodInsnNode(Opcodes.INVOKEVIRTUAL, OWNER, helperName, "(Z)Z", false));
            permission.instructions.add(new InsnNode(Opcodes.POP));
        }
        permission.instructions.add(new InsnNode(Opcodes.ICONST_0)); permission.instructions.add(new InsnNode(Opcodes.IRETURN));
        return write(node);
    }

    private static byte[] positionFixture(boolean foreignCaller, boolean escapePosition, boolean escapeCheck, boolean badAckDescriptor) {
        ClassNode node = read(fixture(true, false, true, "shouldCheck", 1));
        for (MethodNode method : node.methods) {
            if (method.name.equals("player")) {
                method.instructions.clear(); positionCall(method); method.instructions.add(new InsnNode(Opcodes.RETURN));
            }
            if (method.name.equals("permission") && (escapePosition || escapeCheck)) {
                method.instructions.insert(new InsnNode(Opcodes.POP));
                method.instructions.insert(new LdcInsnNode(new Handle(Opcodes.H_INVOKEVIRTUAL, OWNER,
                    escapePosition ? "position" : "shouldCheck", escapePosition ? "(DDDFFZZ)V" : "(Z)Z", false)));
            }
        }
        MethodNode position = method(node, Opcodes.ACC_PRIVATE, "position", "(DDDFFZZ)V");
        position.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0));
        position.instructions.add(new InsnNode(Opcodes.ICONST_0));
        position.instructions.add(new MethodInsnNode(Opcodes.INVOKEVIRTUAL, OWNER, "shouldCheck", "(Z)Z", false));
        position.instructions.add(new InsnNode(Opcodes.POP)); position.instructions.add(new InsnNode(Opcodes.RETURN));
        MethodNode ack = method(node, Opcodes.ACC_PUBLIC, "ack", badAckDescriptor ? "()V" : "(Ljava/lang/Object;)V");
        positionCall(ack); ack.instructions.add(new InsnNode(Opcodes.RETURN));
        if (foreignCaller) {
            MethodNode foreign = method(node, Opcodes.ACC_PRIVATE, "foreign", "()V");
            positionCall(foreign); foreign.instructions.add(new InsnNode(Opcodes.RETURN));
        }
        return write(node);
    }

    private static void positionCall(MethodNode method) {
        method.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0));
        for (int index = 0; index < 3; index++) method.instructions.add(new InsnNode(Opcodes.DCONST_0));
        for (int index = 0; index < 2; index++) method.instructions.add(new InsnNode(Opcodes.FCONST_0));
        for (int index = 0; index < 2; index++) method.instructions.add(new InsnNode(Opcodes.ICONST_0));
        method.instructions.add(new MethodInsnNode(Opcodes.INVOKEVIRTUAL, OWNER, "position", "(DDDFFZZ)V", false));
    }

    private static void ownerGuard(MethodNode method) {
        method.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0));
        method.instructions.add(new MethodInsnNode(Opcodes.INVOKEVIRTUAL, OWNER, "isOwner", "()Z", false));
    }
    private static MethodInsnNode hook() { return new MethodInsnNode(Opcodes.INVOKESTATIC, HOOKS, "trustedLikeTheHost", "(Ljava/lang/Object;)Z", false); }
    private static MethodNode method(ClassNode owner, int access, String name, String descriptor) {
        MethodNode result = new MethodNode(access, name, descriptor, null, null); owner.methods.add(result); return result;
    }
    private static ClassNode read(byte[] data) { ClassNode node = new ClassNode(); new ClassReader(data).accept(node, 0); return node; }
    private static byte[] write(ClassNode node) { ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS); node.accept(writer); return writer.toByteArray(); }
    private static void property(String name, String value) { System.setProperty("minecraft.portable.identity." + name, value); }
}
