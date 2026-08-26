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
import org.objectweb.asm.tree.TypeInsnNode;
import org.objectweb.asm.tree.VarInsnNode;

public final class PortableIdentityTransformer implements ClassFileTransformer {
    private static final String LOGIN_LISTENER =
        "net/minecraft/server/network/ServerLoginPacketListenerImpl";
    private static final String OBFUSCATED_LOGIN_LISTENER = "arw";
    private static final String HELLO_DESCRIPTOR =
        "(Lnet/minecraft/network/protocol/login/ServerboundHelloPacket;)V";
    private static final String OBFUSCATED_HELLO_DESCRIPTOR = "(Laiy;)V";
    private static final String VERIFY_DESCRIPTOR =
        "(Lcom/mojang/authlib/GameProfile;)V";
    private static final String PLAYER_INFO =
        "net/minecraft/client/multiplayer/PlayerInfo";
    private static final String OBFUSCATED_PLAYER_INFO = "fzq";
    // Where authlib keeps the rule about which hosts a skin may come from.
    // It moved once and was renamed once, and those are the only three forms
    // there have ever been: isWhitelistedDomain on the session service up to
    // authlib 2.1.28, isAllowedTextureDomain on the session service from 2.3.31
    // to 3.16.29, and isAllowedTextureDomain on TextureUrlChecker from 3.18.38
    // onwards. Read out of all 18 published authlib jars, 1.5.21 to 9.0.75.
    // Both classes are looked at and whichever carries the method is patched:
    // com.mojang.authlib is never obfuscated, so this is the same on Fabric,
    // Quilt, Forge and NeoForge alike, and needs no mappings from the runtime.
    private static final String TEXTURE_URL_CHECKER =
        "com/mojang/authlib/yggdrasil/TextureUrlChecker,"
            + "com/mojang/authlib/yggdrasil/YggdrasilMinecraftSessionService";
    private static final String TEXTURE_URL_CHECKER_METHODS =
        "isAllowedTextureDomain,isWhitelistedDomain";
    private static final String SKIN_READER_METHODS = "getTextures,getPackedTextures";
    private static final String SKIN_LOOKUP_DESCRIPTOR =
        "(Lcom/mojang/authlib/GameProfile;)Ljava/util/function/Supplier;";
    private static final String SKIN_SELECTION_DESCRIPTOR =
        "(Ljava/util/concurrent/CompletableFuture;Lnet/minecraft/client/resources/PlayerSkin;Z)" +
        "Lnet/minecraft/client/resources/PlayerSkin;";
    private static final String OBFUSCATED_SKIN_SELECTION_DESCRIPTOR =
        "(Ljava/util/concurrent/CompletableFuture;Lgrl;Z)Lgrl;";
    private static final String HOOKS =
        "minecraft/portable/identity/PortableIdentityHooks";
    private static final String SKIN_PROFILES =
        "minecraft/portable/identity/PortableSkinProfiles";

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

    private static boolean matchesMethod(
        MethodNode method,
        String namesProperty,
        String defaultNames,
        String descriptorsProperty,
        String defaultDescriptors) {
        return contains(property(namesProperty, defaultNames), method.name)
            && contains(property(descriptorsProperty, defaultDescriptors), method.desc);
    }

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        String listeners = property(
            "loginClasses",
            LOGIN_LISTENER + "," + OBFUSCATED_LOGIN_LISTENER);
        String playerInfoClasses = property(
            "playerInfoClasses",
            PLAYER_INFO + "," + OBFUSCATED_PLAYER_INFO);
        String textureUrlCheckerClasses = property(
            "textureUrlCheckerClasses",
            TEXTURE_URL_CHECKER);
        // The UUID hooks and the skin hooks are no longer one thing. The UUID
        // ones patch Minecraft's own classes and so need the runtime's
        // mappings; the skin ones are all in com.mojang.authlib and need none.
        // Where the mappings are absent - every Fabric runtime, since Fabric
        // ships intermediary rather than TSRG2 - the launcher switches these
        // off and the skin hooks still run.
        //
        // Switched off by a flag rather than by emptying the alias lists,
        // because an empty property falls back to the built-in defaults, and
        // those name real 1.21.1 classes. On a 1.20.1 runtime, which carries
        // the same unobfuscated class name, the defaults would match and then
        // fail to find the method they expect - killing the game instead of
        // quietly doing nothing.
        boolean identityHooks = !"false".equals(property("identityHooksEnabled", "true"));
        boolean loginClass = identityHooks && contains(listeners, className);
        boolean playerInfoClass = identityHooks && contains(playerInfoClasses, className);
        boolean textureUrlCheckerClass = contains(textureUrlCheckerClasses, className);
        if (!loginClass && !playerInfoClass && !textureUrlCheckerClass) {
            return null;
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);
        if (textureUrlCheckerClass) {
            return transformTextureUrlChecker(node, className);
        }
        if (playerInfoClass) {
            return transformPlayerInfo(node, className);
        }

        boolean helloPatched = false;
        boolean duplicatePatched = false;
        for (MethodNode method : node.methods) {
            if (matchesMethod(
                method,
                "helloMethods",
                "handleHello,a",
                "helloDescriptors",
                HELLO_DESCRIPTOR + "," + OBFUSCATED_HELLO_DESCRIPTOR)) {
                prependGuard(method, "handleHello");
                helloPatched = true;
            } else if (matchesMethod(
                method,
                "verifyMethods",
                "verifyLoginAndFinishConnectionSetup,c",
                "verifyDescriptors",
                VERIFY_DESCRIPTOR)) {
                prependGuard(method, "rejectDuplicateUuid");
                duplicatePatched = true;
            }
        }

        if (!helloPatched || !duplicatePatched) {
            throw new IllegalStateException(
                "Unsupported Minecraft login bytecode: required methods were not found.");
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched login class " + className + ".");
        return writer.toByteArray();
    }

    private static byte[] transformTextureUrlChecker(ClassNode node, String className) {
        boolean checkerPatched = false;
        for (MethodNode method : node.methods) {
            if (isSkinReader(method)) {
                // Where the game asks authlib for a player's skin, whatever
                // version it is. Everything the launcher needs for a skin now
                // happens on this one class: the profile is given its textures
                // property here, a moment before authlib reads it, and the rule
                // about allowed hosts is relaxed a few lines down. Both are
                // com.mojang.authlib, which no loader obfuscates, so this needs
                // no mappings from the runtime and is the same on Fabric,
                // Quilt, Forge and NeoForge.
                //
                // Argument 1 is the GameProfile in both forms: getTextures took
                // it up to authlib 5.0.47 and getPackedTextures takes it from
                // 6.0.52 on, and there has never been a third.
                InsnList inject = new InsnList();
                inject.add(new VarInsnNode(Opcodes.ALOAD, 1));
                inject.add(new MethodInsnNode(
                    Opcodes.INVOKESTATIC,
                    SKIN_PROFILES,
                    "apply",
                    "(Ljava/lang/Object;)V",
                    false));
                method.instructions.insert(inject);
                checkerPatched = true;
                continue;
            }
            if (!matchesMethod(
                method,
                "textureUrlCheckerMethods",
                TEXTURE_URL_CHECKER_METHODS,
                "textureUrlCheckerDescriptors",
                "(Ljava/lang/String;)Z")) {
                continue;
            }

            LabelNode continueLabel = new LabelNode();
            InsnList prefix = new InsnList();
            prefix.add(new VarInsnNode(Opcodes.ALOAD, 0));
            prefix.add(new MethodInsnNode(
                Opcodes.INVOKESTATIC,
                SKIN_PROFILES,
                "isRegisteredUrl",
                "(Ljava/lang/String;)Z",
                false));
            prefix.add(new JumpInsnNode(Opcodes.IFEQ, continueLabel));
            prefix.add(new InsnNode(Opcodes.ICONST_1));
            prefix.add(new InsnNode(Opcodes.IRETURN));
            prefix.add(continueLabel);
            prefix.add(new FrameNode(Opcodes.F_SAME, 0, null, 0, null));
            method.instructions.insert(prefix);
            checkerPatched = true;
        }

        if (!checkerPatched) {
            // Not a failure. Both candidate classes are offered to this method,
            // and on any given authlib exactly one of them carries the rule -
            // the other is an ordinary class that must be handed back untouched.
            return null;
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched authlib skin class " + className + ".");
        return writer.toByteArray();
    }

    // The method the game calls to read a profile's skin. Matched by name and
    // by taking a GameProfile first, so an unrelated overload cannot be caught
    // by accident.
    private static boolean isSkinReader(MethodNode method) {
        return contains(property("skinReaderMethods", SKIN_READER_METHODS), method.name)
            && method.desc.startsWith("(Lcom/mojang/authlib/GameProfile;");
    }

    private static byte[] transformPlayerInfo(ClassNode node, String className) {
        boolean lookupPatched = false;
        boolean selectionPatched = false;
        for (MethodNode method : node.methods) {
            if (matchesMethod(
                method,
                "skinLookupMethods",
                "createSkinLookup,a",
                "skinLookupDescriptors",
                SKIN_LOOKUP_DESCRIPTOR)) {
                prependSkinRegistration(method);
                lookupPatched = true;
            } else if (matchesMethod(
                method,
                "skinSelectionMethods",
                "lambda$createSkinLookup$2,a",
                "skinSelectionDescriptors",
                SKIN_SELECTION_DESCRIPTOR + "," + OBFUSCATED_SKIN_SELECTION_DESCRIPTOR)) {
                replaceSkinSelection(method);
                selectionPatched = true;
            }
        }

        if (!lookupPatched || !selectionPatched) {
            throw new IllegalStateException(
                "Unsupported Minecraft skin bytecode: required methods were not found.");
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched player skin class " + className + ".");
        return writer.toByteArray();
    }

    private static void prependGuard(MethodNode method, String hookName) {
        LabelNode continueLabel = new LabelNode();
        InsnList prefix = new InsnList();
        prefix.add(new VarInsnNode(Opcodes.ALOAD, 0));
        prefix.add(new VarInsnNode(Opcodes.ALOAD, 1));
        prefix.add(new MethodInsnNode(
            Opcodes.INVOKESTATIC,
            HOOKS,
            hookName,
            "(Ljava/lang/Object;Ljava/lang/Object;)Z",
            false));
        prefix.add(new JumpInsnNode(Opcodes.IFEQ, continueLabel));
        prefix.add(new InsnNode(Opcodes.RETURN));
        prefix.add(continueLabel);
        prefix.add(new FrameNode(Opcodes.F_SAME, 0, null, 0, null));
        method.instructions.insert(prefix);
    }

    private static void prependSkinRegistration(MethodNode method) {
        InsnList prefix = new InsnList();
        prefix.add(new VarInsnNode(Opcodes.ALOAD, 0));
        prefix.add(new MethodInsnNode(
            Opcodes.INVOKESTATIC,
            SKIN_PROFILES,
            "apply",
            "(Ljava/lang/Object;)V",
            false));
        method.instructions.insert(prefix);
    }

    private static void replaceSkinSelection(MethodNode method) {
        int returnStart = method.desc.lastIndexOf(')') + 1;
        String returnDescriptor = method.desc.substring(returnStart);
        if (!returnDescriptor.startsWith("L") || !returnDescriptor.endsWith(";")) {
            throw new IllegalStateException("Portable skin selector has an unsupported return type.");
        }
        String returnType = returnDescriptor.substring(1, returnDescriptor.length() - 1);

        method.instructions.clear();
        method.tryCatchBlocks.clear();
        if (method.localVariables != null) {
            method.localVariables.clear();
        }
        method.visibleLocalVariableAnnotations = null;
        method.invisibleLocalVariableAnnotations = null;

        method.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0));
        method.instructions.add(new VarInsnNode(Opcodes.ALOAD, 1));
        method.instructions.add(new VarInsnNode(Opcodes.ILOAD, 2));
        method.instructions.add(new MethodInsnNode(
            Opcodes.INVOKESTATIC,
            SKIN_PROFILES,
            "selectSkin",
            "(Ljava/lang/Object;Ljava/lang/Object;Z)Ljava/lang/Object;",
            false));
        method.instructions.add(new TypeInsnNode(Opcodes.CHECKCAST, returnType));
        method.instructions.add(new InsnNode(Opcodes.ARETURN));
    }
}
