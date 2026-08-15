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

public final class PortableXaeroWaypointTransformer implements ClassFileTransformer {
    private static final String HOOKS =
        "minecraft/portable/identity/PortableXaeroWaypointHooks";
    private static final String DEFAULT_TARGET =
        "xaero/hud/minimap/waypoint/WaypointTeleport";
    private static final String DEFAULT_TELEPORT_DESCRIPTOR =
        "(Lxaero/common/minimap/waypoints/Waypoint;" +
        "Lxaero/hud/minimap/world/MinimapWorld;" +
        "Lnet/minecraft/client/gui/screens/Screen;Z)V";

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        if (!Boolean.getBoolean("minecraft.portable.identity.xaeroWaypointEnabled") ||
            !contains(property("xaeroWaypointTeleportClasses", DEFAULT_TARGET), className)) {
            return null;
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);
        int unsignedCallsPatched = 0;
        int fallbackCallsPatched = 0;
        for (MethodNode method : node.methods) {
            if (!matchesMethod(
                method,
                "xaeroWaypointTeleportMethods",
                "teleportToWaypoint",
                "xaeroWaypointTeleportDescriptors",
                DEFAULT_TELEPORT_DESCRIPTOR)) {
                continue;
            }

            for (AbstractInsnNode instruction = method.instructions.getFirst();
                 instruction != null;
                 instruction = instruction.getNext()) {
                if (!(instruction instanceof MethodInsnNode call) ||
                    call.getOpcode() != Opcodes.INVOKEVIRTUAL ||
                    !contains(
                        property(
                            "clientPacketListenerClasses",
                            "net/minecraft/client/multiplayer/ClientPacketListener,fzg"),
                        call.owner)) {
                    continue;
                }

                boolean unsigned = contains(
                    property("sendUnsignedCommandMethods", "sendUnsignedCommand,d"),
                    call.name) && call.desc.equals("(Ljava/lang/String;)Z");
                boolean fallback = contains(
                    property("sendCommandMethods", "sendCommand,c"),
                    call.name) && call.desc.equals("(Ljava/lang/String;)V");
                if (!unsigned && !fallback) {
                    continue;
                }

                method.instructions.insertBefore(call, new MethodInsnNode(
                    Opcodes.INVOKESTATIC,
                    HOOKS,
                    "rewriteCrossDimensionCommand",
                    "(Ljava/lang/String;)Ljava/lang/String;",
                    false));
                if (unsigned) {
                    unsignedCallsPatched++;
                } else {
                    fallbackCallsPatched++;
                }
            }
        }

        if (unsignedCallsPatched != 1 || fallbackCallsPatched != 1) {
            throw new IllegalStateException(
                "Unsupported Xaero waypoint teleport bytecode: expected one unsigned command " +
                "call and one fallback command call, found " + unsignedCallsPatched +
                " and " + fallbackCallsPatched + ".");
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched Xaero waypoint teleport class " + className + ".");
        return writer.toByteArray();
    }

    private static boolean matchesMethod(
        MethodNode method,
        String namesProperty,
        String defaultNames,
        String descriptorsProperty,
        String defaultDescriptors) {
        return contains(property(namesProperty, defaultNames), method.name)
            && contains(property(descriptorsProperty, defaultDescriptors), method.desc);
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
