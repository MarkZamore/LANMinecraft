package minecraft.portable.identity;

import java.io.InputStream;
import java.lang.instrument.ClassFileTransformer;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.FieldNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;

public final class PortableIdentityPreflight {
    private PortableIdentityPreflight() {
    }

    public static void main(String[] arguments) throws Exception {
        if (arguments.length != 2) {
            throw new IllegalArgumentException("Usage: <runtime-jar> <internal-class-name>");
        }

        Path jarPath = Path.of(arguments[0]).toAbsolutePath().normalize();
        String className = arguments[1].replace('.', '/');
        try (ZipFile archive = new ZipFile(jarPath.toFile())) {
            byte[] original = readClassBytes(archive, className);

            ClassFileTransformer transformer;
            if (isAlias("ftbTeleportClasses", className)) {
                transformer = new PortableFtbTeleportTransformer();
            } else if (isAlias("solarFluxPackClasses", className)) {
                transformer = new PortableSolarFluxSyncTransformer();
            } else if (isAlias("xaeroWaypointTeleportClasses", className)) {
                transformer = new PortableXaeroWaypointTransformer();
            } else if (isAlias("lanShareScreenClasses", className)) {
                transformer = new PortableLanAutoPublishTransformer();
            } else {
                transformer = new PortableIdentityTransformer();
            }
            byte[] transformed = transformer.transform(
                null,
                null,
                className,
                null,
                null,
                original);
            if (transformed == null || Arrays.equals(original, transformed)) {
                throw new IllegalStateException("Portable identity transformer did not modify " + className + ".");
            }
            new ClassReader(transformed);
            if (isAlias("loginClasses", className)) {
                verifyHookTargets(archive, className);
            } else if (isAlias("ftbTeleportClasses", className)) {
                verifyFtbTeleportTargets(archive, className, transformed);
            } else if (isAlias("solarFluxPackClasses", className)) {
                verifySolarFluxTargets(className, transformed);
            } else if (isAlias("xaeroWaypointTeleportClasses", className)) {
                verifyXaeroWaypointTargets(archive, className, transformed);
            } else if (isAlias("lanShareScreenClasses", className)) {
                verifyLanSharePublishTargets(archive, className, transformed);
            } else if (!isAlias("playerInfoClasses", className) &&
                !isAlias("textureUrlCheckerClasses", className) &&
                !isAlias("gameProfileClasses", className)) {
                throw new IllegalStateException("Unexpected portable identity target: " + className);
            }
        }
        System.out.println("Portable identity preflight passed: " + className + " in " + jarPath);
    }

    /**
     * The one-press publish: one call to the hook, and every member the hook
     * reaches for through reflection, in the mapping this class was found under.
     */
    private static void verifyLanSharePublishTargets(
        ZipFile archive,
        String screenClass,
        byte[] transformed) throws Exception {
        int mappingIndex = aliasIndex("lanShareScreenClasses", screenClass);
        ClassNode screen = readClass(archive, screenClass);
        requireMethod(screen, "lanShareInitMethods", 0);

        ClassNode patched = new ClassNode();
        new ClassReader(transformed).accept(patched, 0);
        int hookCalls = 0;
        for (MethodNode method : patched.methods) {
            for (var instruction = method.instructions.getFirst();
                 instruction != null;
                 instruction = instruction.getNext()) {
                if (instruction instanceof MethodInsnNode call &&
                    call.owner.equals("minecraft/portable/identity/PortableLanAutoPublishHooks") &&
                    call.name.equals("autoPublish") &&
                    call.desc.equals("(Ljava/lang/Object;)Z")) {
                    hookCalls++;
                }
            }
        }
        if (hookCalls != 1) {
            throw new IllegalStateException(
                "LAN share screen transformer inserted " + hookCalls + " publish hooks instead of 1.");
        }

        ClassNode integrated = readClass(archive, alias("integratedServerClasses", mappingIndex));
        requireMethod(integrated, "publishServerMethods", 3);

        ClassNode server = readClass(archive, alias("serverClasses", mappingIndex));
        requireMethod(server, "getWorldDataMethods", 0);
        requireMethod(server, "isPublishedMethods", 0);
        requireMethod(server, "getDefaultGameTypeMethods", 0);

        ClassNode worldData = readClass(archive, alias("worldDataClasses", mappingIndex));
        requireMethod(worldData, "isAllowCommandsMethods", 0);

        ClassNode httpUtil = readClass(archive, alias("httpUtilClasses", mappingIndex).replace('.', '/'));
        requireMethod(httpUtil, "getAvailablePortMethods", 0);

        ClassNode minecraft = readClass(archive, alias("minecraftClasses", mappingIndex).replace('.', '/'));
        requireMethod(minecraft, "minecraftGetInstanceMethods", 0);
        requireMethod(minecraft, "getSingleplayerServerMethods", 0);
        requireMethod(minecraft, "setScreenMethods", 1);
        requireMethod(minecraft, "updateTitleMethods", 0);
        requireField(minecraft, "minecraftGuiFields");

        ClassNode gui = readClass(archive, alias("guiClasses", mappingIndex));
        requireMethod(gui, "guiChatMethods", 0);

        ClassNode chat = readClass(archive, alias("chatComponentClasses", mappingIndex));
        requireMethod(chat, "chatAddMessageMethods", 1);

        ClassNode publishCommand = readClass(archive, alias("publishCommandClasses", mappingIndex).replace('.', '/'));
        // The line the game writes in chat when a world opens is wanted, not
        // required: PublishCommand said it inline until 1.19.4, so on an older
        // Minecraft the launcher does not name it at all. Demanding it here
        // failed the whole preflight, and a failed preflight is no adapter -
        // which cost All The Fabric 3 its skins and its stable UUID over a line
        // of chat.
        if (isConfigured("publishSuccessMethods")) {
            requireMethod(publishCommand, "publishSuccessMethods", 1);
        }

        readClass(archive, alias("gameTypeClasses", mappingIndex).replace('.', '/'));
        readClass(archive, alias("screenClasses", mappingIndex).replace('.', '/'));
    }

    private static void verifyHookTargets(ZipFile archive, String loginClass) throws Exception {
        int mappingIndex = aliasIndex("loginClasses", loginClass);
        ClassNode listener = readClass(archive, loginClass);
        requireField(listener, "serverFields");
        requireField(listener, "connectionFields");
        requireField(listener, "requestedUsernameFields");
        requireMethod(listener, "startVerificationMethods", 1);
        requireMethod(listener, "disconnectMethods", 1);

        ClassNode packet = readClass(archive, alias("packetClasses", mappingIndex));
        requireMethod(packet, "packetNameMethods", 0);
        requireMethod(packet, "packetUuidMethods", 0);

        ClassNode connection = readClass(archive, alias("connectionClasses", mappingIndex));
        requireMethod(connection, "memoryConnectionMethods", 0);

        ClassNode server = readClass(archive, alias("serverClasses", mappingIndex));
        requireMethod(server, "playerListMethods", 0);

        ClassNode playerList = readClass(archive, alias("playerListClasses", mappingIndex));
        requireMethod(playerList, "getPlayerMethods", 1);

        ClassNode component = readClass(archive, alias("componentClasses", mappingIndex).replace('.', '/'));
        requireMethod(component, "componentLiteralMethods", 1);
    }

    private static void verifyFtbTeleportTargets(
        ZipFile archive,
        String teleportClass,
        byte[] transformed) throws Exception {
        ClassNode original = readClassWithCode(archive, teleportClass);
        if (countPermissionChecks(original) != 1) {
            throw new IllegalStateException(
                "FTB Chunks teleport class " + teleportClass + " does not contain exactly one permission check.");
        }

        ClassNode patched = new ClassNode();
        new ClassReader(transformed).accept(patched, 0);
        if (countPermissionChecks(patched) != 0) {
            throw new IllegalStateException(
                "FTB Chunks teleport transformer left a permission check in " + teleportClass + ".");
        }
    }

    private static ClassNode readClassWithCode(ZipFile archive, String className) throws Exception {
        ClassNode node = new ClassNode();
        new ClassReader(readClassBytes(archive, className)).accept(node, 0);
        return node;
    }

    private static int countPermissionChecks(ClassNode node) {
        String[] names = aliases("ftbPermissionMethods");
        int count = 0;
        for (MethodNode method : node.methods) {
            for (AbstractInsnNode instruction = method.instructions.getFirst();
                 instruction != null;
                 instruction = instruction.getNext()) {
                if (!(instruction instanceof MethodInsnNode)) {
                    continue;
                }
                MethodInsnNode call = (MethodInsnNode) instruction;
                if (Arrays.asList(names).contains(call.name) &&
                    call.desc.equals("(I)Z")) {
                    count++;
                }
            }
        }
        return count;
    }

    private static void verifySolarFluxTargets(String packClass, byte[] transformed) {
        ClassNode patched = new ClassNode();
        new ClassReader(transformed).accept(patched, ClassReader.SKIP_CODE);
        String[] names = aliases("solarFluxSyncMethods");
        int synchronizedMethods = 0;
        for (MethodNode method : patched.methods) {
            if (Arrays.asList(names).contains(method.name) &&
                (method.access & Opcodes.ACC_SYNCHRONIZED) != 0) {
                synchronizedMethods++;
            }
        }
        if (synchronizedMethods != 4) {
            throw new IllegalStateException(
                "SolarFlux transformer synchronized " + synchronizedMethods +
                " methods of " + packClass + " instead of 4.");
        }
    }

    private static void verifyXaeroWaypointTargets(
        ZipFile archive,
        String waypointClass,
        byte[] transformed) throws Exception {
        ClassNode original = readClass(archive, waypointClass);
        requireMethod(original, "xaeroWaypointTeleportMethods", 4);

        ClassNode patched = new ClassNode();
        new ClassReader(transformed).accept(patched, 0);
        int hookCalls = 0;
        for (MethodNode method : patched.methods) {
            for (AbstractInsnNode instruction = method.instructions.getFirst();
                 instruction != null;
                 instruction = instruction.getNext()) {
                if (!(instruction instanceof MethodInsnNode)) {
                    continue;
                }
                MethodInsnNode call = (MethodInsnNode) instruction;
                if (call.owner.equals("minecraft/portable/identity/PortableXaeroWaypointHooks") &&
                    call.name.equals("rewriteCrossDimensionCommand") &&
                    call.desc.equals("(Ljava/lang/String;)Ljava/lang/String;")) {
                    hookCalls++;
                }
            }
        }
        if (hookCalls != 2) {
            throw new IllegalStateException(
                "Xaero waypoint transformer inserted " + hookCalls + " command hooks instead of 2.");
        }

        assertWaypointRewrite(
            "execute in minecraft:the_nether run portable_waypoint_tp -12 64.5 8",
            "portable_waypoint_tp_dimension minecraft:the_nether -12 64.5 8");
        assertWaypointRewrite(
            "/execute in example.mod:moon/base run portable_waypoint_tp -1 -0.5 2 90",
            "portable_waypoint_tp_dimension example.mod:moon/base -1 -0.5 2 90");

        assertWaypointUnchanged("portable_waypoint_tp -12 64 8");
        assertWaypointUnchanged("execute in minecraft:overworld run tp @s 1 2 3");
        assertWaypointUnchanged("execute  in minecraft:overworld run portable_waypoint_tp 1 2 3");
        assertWaypointUnchanged("execute in Minecraft:overworld run portable_waypoint_tp 1 2 3");
        assertWaypointUnchanged("execute in minecraft:overworld run portable_waypoint_tp 1 ~ 3");
        assertWaypointUnchanged("execute in minecraft:overworld run portable_waypoint_tp 1 2 3 4 extra");
    }

    private static void assertWaypointRewrite(String input, String expected) {
        String actual = PortableXaeroWaypointHooks.rewriteCrossDimensionCommand(input);
        if (!expected.equals(actual)) {
            throw new IllegalStateException(
                "Xaero waypoint command rewrite mismatch: expected '" + expected +
                "', got '" + actual + "'.");
        }
    }

    private static void assertWaypointUnchanged(String input) {
        String actual = PortableXaeroWaypointHooks.rewriteCrossDimensionCommand(input);
        if (!input.equals(actual)) {
            throw new IllegalStateException(
                "Xaero waypoint command escaped fail-closed validation: '" + input + "'.");
        }
    }

    private static byte[] readClassBytes(ZipFile archive, String className) throws Exception {
        ZipEntry entry = archive.getEntry(className + ".class");
        if (entry == null) {
            throw new IllegalStateException("Required identity class is missing: " + className);
        }
        try (InputStream input = archive.getInputStream(entry)) {
            return input.readAllBytes();
        }
    }

    private static ClassNode readClass(ZipFile archive, String className) throws Exception {
        ClassNode node = new ClassNode();
        new ClassReader(readClassBytes(archive, className)).accept(node, ClassReader.SKIP_CODE);
        return node;
    }

    private static void requireField(ClassNode owner, String propertyName) {
        String[] names = aliases(propertyName);
        for (FieldNode field : owner.fields) {
            if (Arrays.asList(names).contains(field.name)) {
                return;
            }
        }
        throw new IllegalStateException("Required identity field is missing: " + owner.name + "." + String.join("/", names));
    }

    private static void requireMethod(ClassNode owner, String propertyName, int argumentCount) {
        String[] names = aliases(propertyName);
        for (MethodNode method : owner.methods) {
            if (Arrays.asList(names).contains(method.name) && argumentCount(method.desc) == argumentCount) {
                return;
            }
        }
        throw new IllegalStateException("Required identity method is missing: " + owner.name + "." + String.join("/", names));
    }

    private static int argumentCount(String descriptor) {
        int count = 0;
        for (int index = 1; descriptor.charAt(index) != ')'; index++) {
            while (descriptor.charAt(index) == '[') index++;
            if (descriptor.charAt(index) == 'L') index = descriptor.indexOf(';', index);
            count++;
        }
        return count;
    }

    private static int aliasIndex(String propertyName, String value) {
        String[] values = aliases(propertyName);
        for (int index = 0; index < values.length; index++) {
            if (values[index].equals(value)) return index;
        }
        throw new IllegalStateException("Runtime identity alias is not configured: " + value);
    }

    private static String alias(String propertyName, int index) {
        String[] values = aliases(propertyName);
        return values[Math.min(index, values.length - 1)];
    }

    private static boolean isAlias(String propertyName, String value) {
        return Arrays.asList(aliases(propertyName)).contains(value);
    }

    /** Whether the launcher named this list at all; some of them are version-dependent. */
    private static boolean isConfigured(String propertyName) {
        String value = System.getProperty("minecraft.portable.identity." + propertyName);
        return value != null && !value.isBlank();
    }

    private static String[] aliases(String propertyName) {
        String value = System.getProperty("minecraft.portable.identity." + propertyName);
        if (value == null || value.isBlank()) {
            throw new IllegalStateException("Identity preflight property is missing: " + propertyName);
        }
        return Arrays.stream(value.split(",")).map(String::trim).filter(item -> !item.isEmpty()).toArray(String[]::new);
    }
}
