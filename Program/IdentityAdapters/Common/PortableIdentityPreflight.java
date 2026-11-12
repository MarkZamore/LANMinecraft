package minecraft.portable.identity;

import java.io.InputStream;
import java.lang.instrument.ClassFileTransformer;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.HashMap;
import java.util.Map;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.FieldInsnNode;
import org.objectweb.asm.tree.FieldNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.LdcInsnNode;
import org.objectweb.asm.tree.MethodNode;
import org.objectweb.asm.tree.VarInsnNode;

public final class PortableIdentityPreflight {
    private static final Map<String, Path> classSources = new HashMap<>();
    private PortableIdentityPreflight() {
    }

    public static void main(String[] arguments) throws Exception {
        if (arguments.length < 2) {
            throw new IllegalArgumentException("Usage: <runtime-jar> <internal-class-name> [<support-class>=<jar> ...]");
        }

        Path jarPath = Path.of(arguments[0]).toAbsolutePath().normalize();
        String className = arguments[1].replace('.', '/');
        classSources.clear();
        for (int index = 2; index < arguments.length; index++) {
            int separator = arguments[index].indexOf('=');
            if (separator <= 0 || separator == arguments[index].length() - 1)
                throw new IllegalArgumentException("Invalid native preflight class source.");
            String name = arguments[index].substring(0, separator);
            if (!name.matches("[A-Za-z0-9_$]+(?:/[A-Za-z0-9_$]+)*"))
                throw new IllegalArgumentException("Invalid native preflight class name.");
            Path source = Path.of(arguments[index].substring(separator + 1)).toAbsolutePath().normalize();
            if (classSources.putIfAbsent(name, source) != null)
                throw new IllegalArgumentException("Duplicate native preflight class source: " + name);
        }
        if (classSources.containsKey(className) && !classSources.get(className).equals(jarPath))
            throw new IllegalArgumentException("Native preflight target source differs from its target JAR.");
        try (ZipFile archive = new ZipFile(jarPath.toFile())) {
            byte[] original = readClassBytes(archive, className);

            ClassFileTransformer transformer;
            if (PortableForgeHandshakeTransformer.TARGET.equals(className) &&
                    Boolean.getBoolean("minecraft.portable.identity.forgeHandshakeSyncEnabled")) {
                transformer = new PortableForgeHandshakeTransformer();
            } else if (isConfigured("windowTitleClasses") && isAlias("windowTitleClasses", className)) {
                transformer = new PortableWindowTitleTransformer();
            } else if (PortableProfileNameTransformer.isTarget(className)) {
                transformer = new PortableProfileNameTransformer();
            } else if (isAlias("ftbTeleportClasses", className)) {
                transformer = new PortableFtbTeleportTransformer();
            } else if (isAlias("solarFluxPackClasses", className)) {
                transformer = new PortableSolarFluxSyncTransformer();
            } else if (isAlias("xaeroWaypointTeleportClasses", className)) {
                transformer = new PortableXaeroWaypointTransformer();
            } else if (isConfigured("pauseScreenClasses") && isAlias("pauseScreenClasses", className)) {
                transformer = new PortableLanAutoPublishTransformer();
            } else if (isAlias("lanShareScreenClasses", className)) {
                transformer = new PortableLanAutoPublishTransformer();
            } else if (PortableLanWorldPolicyTransformer.isTarget(className)) {
                transformer = new PortableLanWorldPolicyTransformer();
            } else if (PortableSteamDiscoveryTransformer.isPublicationTarget(className)) {
                transformer = new PortableSteamDiscoveryTransformer();
            } else if (isConfigured("steamDiscoveryScreenClasses") && isAlias("steamDiscoveryScreenClasses", className)) {
                transformer = new PortableSteamDiscoveryTransformer();
            } else if (isConfigured("steamDiscoveryLanListClasses") && isAlias("steamDiscoveryLanListClasses", className)) {
                transformer = new PortableSteamDiscoveryTransformer();
            } else if (isConfigured("steamDiscoveryLanEntryClasses") && isAlias("steamDiscoveryLanEntryClasses", className)) {
                transformer = new PortableSteamLanEntryTransformer();
            } else if (isAlias("movementListenerClasses", className)) {
                transformer = new PortableGuestMovementTransformer();
            } else if (isAlias("chunkMapClasses", className) ||
                isAlias("serverPlayerClasses", className) ||
                isAlias("trackedEntityClasses", className)) {
                transformer = new PortablePerPlayerChunksTransformer();
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
            if (PortableForgeHandshakeTransformer.TARGET.equals(className) &&
                    Boolean.getBoolean("minecraft.portable.identity.forgeHandshakeSyncEnabled")) {
                PortableForgeHandshakeTransformer.verify(original, transformed);
            } else if (isConfigured("windowTitleClasses") && isAlias("windowTitleClasses", className)) {
                verifyWindowTitleTarget(className, original, transformed);
            } else if (PortableProfileNameTransformer.isTarget(className)) {
                // Exact method shape is checked by the optional transformer;
                // it reaches only a bootstrap hook, no game-specific members.
            } else if (isAlias("loginClasses", className)) {
                verifyHookTargets(archive, className);
            } else if (isAlias("playerInfoClasses", className)) {
                verifyPlayerInfoSkinTargets(archive, className, original, transformed);
            } else if (isAlias("ftbTeleportClasses", className)) {
                verifyFtbTeleportTargets(archive, className, transformed);
            } else if (isAlias("solarFluxPackClasses", className)) {
                verifySolarFluxTargets(className, transformed);
            } else if (isAlias("xaeroWaypointTeleportClasses", className)) {
                verifyXaeroWaypointTargets(archive, className, transformed);
            } else if (isConfigured("pauseScreenClasses") && isAlias("pauseScreenClasses", className)) {
                verifyPauseReportingTargets(archive, className, transformed);
            } else if (isAlias("lanShareScreenClasses", className)) {
                verifyLanSharePublishTargets(archive, className, transformed);
            } else if (PortableLanWorldPolicyTransformer.isTarget(className)) {
                PortableLanWorldPolicyTransformer.verify(original, transformed, className);
            } else if (PortableSteamDiscoveryTransformer.isPublicationTarget(className)) {
                verifyNativeSteamPublication(archive, className, original, transformed);
            } else if (isConfigured("steamDiscoveryScreenClasses") && isAlias("steamDiscoveryScreenClasses", className)) {
                verifySteamDiscoveryTargets(archive, className, transformed);
            } else if (isConfigured("steamDiscoveryLanListClasses") && isAlias("steamDiscoveryLanListClasses", className)) {
                verifySteamDiscoveryList(archive, className, transformed);
            } else if (isConfigured("steamDiscoveryLanEntryClasses") && isAlias("steamDiscoveryLanEntryClasses", className)) {
                verifySteamDiscoveryEntry(transformed);
            } else if (isAlias("movementListenerClasses", className)) {
                verifyGuestMovementTargets(className, original, transformed);
            } else if (isAlias("chunkMapClasses", className) ||
                isAlias("serverPlayerClasses", className) ||
                isAlias("trackedEntityClasses", className)) {
                verifyPerPlayerChunkTargets(archive, className, transformed);
            } else if (!isAlias("playerInfoClasses", className) &&
                !(isConfigured("skinManagerClasses") && isAlias("skinManagerClasses", className)) &&
                !isAlias("textureUrlCheckerClasses", className) &&
                !isAlias("gameProfileClasses", className)) {
                throw new IllegalStateException("Unexpected portable identity target: " + className);
            }
        }
        System.out.println("Portable identity preflight passed: " + className + " in " + jarPath);
    }

    /** The independent skin family changes exactly the mapped lookup and selector. */
    private static void verifyPlayerInfoSkinTargets(
            ZipFile archive, String className, byte[] original, byte[] transformed) throws Exception {
        ClassNode before = new ClassNode();
        new ClassReader(original).accept(before, 0);
        ClassNode after = new ClassNode();
        new ClassReader(transformed).accept(after, 0);
        if (!className.equals(before.name) || !className.equals(after.name) || before.methods.size() != after.methods.size())
            throw new IllegalStateException("PlayerInfo skin transformer changed the class or method set.");
        int changed = 0;
        MethodNode selection = null;
        for (int index = 0; index < before.methods.size(); index++) {
            MethodNode old = before.methods.get(index);
            MethodNode current = after.methods.get(index);
            if (!old.name.equals(current.name) || !old.desc.equals(current.desc) || old.access != current.access)
                throw new IllegalStateException("PlayerInfo skin transformer changed a native signature.");
            if (Arrays.equals(methodBytes(old), methodBytes(current))) continue;
            changed++;
            if ((old.access & Opcodes.ACC_STATIC) == 0)
                throw new IllegalStateException("PlayerInfo skin helper is not a native static factory.");
            if (isAlias("skinLookupMethods", old.name) && isAlias("skinLookupDescriptors", old.desc)) {
                AbstractInsnNode first = current.instructions.getFirst();
                if (!(first instanceof org.objectweb.asm.tree.VarInsnNode load) || load.getOpcode() != Opcodes.ALOAD || load.var != 0 ||
                        !(first.getNext() instanceof MethodInsnNode hook) || hook.getOpcode() != Opcodes.INVOKESTATIC ||
                        !hook.owner.equals("minecraft/portable/identity/PortableSkinProfiles") || !hook.name.equals("apply") ||
                        !hook.desc.equals("(Ljava/lang/Object;)V"))
                    throw new IllegalStateException("PlayerInfo lookup lacks the exact registered-profile hook.");
            } else if (isAlias("skinSelectionMethods", old.name) && isAlias("skinSelectionDescriptors", old.desc)) {
                selection = old;
                int hooks = 0;
                for (AbstractInsnNode instruction : current.instructions)
                    if (instruction instanceof MethodInsnNode hook && hook.getOpcode() == Opcodes.INVOKESTATIC &&
                            hook.owner.equals("minecraft/portable/identity/PortableSkinProfiles") && hook.name.equals("selectSkin") &&
                            hook.desc.equals("(Ljava/lang/Object;Ljava/lang/Object;Z)Ljava/lang/Object;")) hooks++;
                if (hooks != 1) throw new IllegalStateException("PlayerInfo selector lacks the exact registry guard.");
            } else throw new IllegalStateException("PlayerInfo skin transformer changed an unrelated method: " + old.name);
        }
        if (changed != 2 || selection == null)
            throw new IllegalStateException("PlayerInfo skin transformer must change one lookup and one selector.");
        String result = selection.desc.substring(selection.desc.lastIndexOf(')') + 1);
        if (!result.startsWith("L") || !result.endsWith(";"))
            throw new IllegalStateException("PlayerInfo selector has no native PlayerSkin return type.");
        ClassNode skin = readClass(archive, result.substring(1, result.length() - 1));
        boolean secure = false, url = false;
        for (MethodNode method : skin.methods) {
            if ((method.access & Opcodes.ACC_STATIC) != 0) continue;
            secure |= isAlias("skinSecureMethods", method.name) && method.desc.equals("()Z");
            url |= isAlias("skinTextureUrlMethods", method.name) && method.desc.equals("()Ljava/lang/String;");
        }
        if (!secure || !url) throw new IllegalStateException("PlayerInfo skin guard has no exact native secure/textureUrl accessors.");
    }

    /** Prove that only one native String producer changed, never updateTitle or networking. */
    private static void verifyWindowTitleTarget(String className, byte[] original, byte[] transformed) {
        ClassNode before = new ClassNode();
        new ClassReader(original).accept(before, 0);
        ClassNode after = new ClassNode();
        new ClassReader(transformed).accept(after, 0);
        if (!className.equals(before.name) || !className.equals(after.name) ||
            before.methods.size() != after.methods.size()) {
            throw new IllegalStateException("Window title transformer changed the class/method set.");
        }
        int changed = 0;
        for (int index = 0; index < before.methods.size(); index++) {
            MethodNode old = before.methods.get(index);
            MethodNode current = after.methods.get(index);
            if (!old.name.equals(current.name) || !old.desc.equals(current.desc) || old.access != current.access) {
                throw new IllegalStateException("Window title transformer changed a method signature.");
            }
            if (Arrays.equals(methodBytes(old), methodBytes(current))) continue;
            changed++;
            if (!isAlias("createTitleMethods", old.name) || !old.desc.equals("()Ljava/lang/String;")) {
                throw new IllegalStateException("Window title transformer changed a non-title method: " + old.name);
            }
            AbstractInsnNode first = current.instructions.getFirst();
            if (!(first instanceof LdcInsnNode text) ||
                !System.getProperty("minecraft.portable.identity.windowTitleText", "").equals(text.cst) ||
                first.getNext() == null || first.getNext().getOpcode() != Opcodes.ARETURN ||
                first.getNext().getNext() != null || !current.tryCatchBlocks.isEmpty()) {
                throw new IllegalStateException("Window title producer is not a constant String return.");
            }
        }
        if (changed != 1) throw new IllegalStateException("Window title transformer changed " + changed + " methods, expected 1.");
    }

    private static byte[] methodBytes(MethodNode method) {
        ClassWriter writer = new ClassWriter(0);
        writer.visit(Opcodes.V17, Opcodes.ACC_PUBLIC, "PortableTitleMethodProof", null, "java/lang/Object", null);
        method.accept(writer);
        writer.visitEnd();
        return writer.toByteArray();
    }

    private static void verifyPauseReportingTargets(
        ZipFile archive, String className, byte[] transformed) throws Exception {
        if (Boolean.getBoolean("minecraft.portable.identity.pauseLegacyReportingEnabled")) {
            verifyLegacyPauseReportingTargets(archive, className, transformed);
            return;
        }
        if ("true".equals(System.getProperty("minecraft.portable.identity.pauseLanRowEnabled"))) {
            verifyLanPauseRowTargets(archive, className, transformed);
            return;
        }
        int mappingIndex = aliasIndex("pauseScreenClasses", className);
        ClassNode pause = readClass(archive, className);
        requireMethod(pause, "pauseCreateMenuMethods", 0);
        String componentDescriptor = "L" + alias("componentClasses", mappingIndex).replace('.', '/') + ";";
        for (String property : new String[] { "pauseMultiplayerFields", "pausePlayerReportingFields" }) {
            boolean nativeLabelFound = false;
            for (FieldNode field : pause.fields) {
                if (isAlias(property, field.name) && (field.access & Opcodes.ACC_STATIC) != 0 &&
                    field.desc.equals(componentDescriptor)) nativeLabelFound = true;
            }
            if (!nativeLabelFound) {
                throw new IllegalStateException("The native pause label is missing: " + pause.name + "." + property);
            }
        }
        requireMethodDescriptor(pause, "pauseOpenScreenButtonMethods", "pauseOpenScreenButtonDescriptors");

        ClassNode reporting = readClass(archive,
            alias("reportingScreenClasses", mappingIndex).replace('.', '/'));
        String screen = alias("screenClasses", mappingIndex).replace('.', '/');
        boolean constructorFound = false;
        for (MethodNode method : reporting.methods) {
            if (method.name.equals("<init>") && method.desc.equals("(L" + screen + ";)V")) {
                constructorFound = true;
            }
        }
        if (!constructorFound) {
            throw new IllegalStateException("The native reporting screen constructor is missing: " + reporting.name);
        }

        ClassNode patched = new ClassNode();
        new ClassReader(transformed).accept(patched, 0);
        int hooks = 0;
        for (MethodNode method : patched.methods) {
            for (AbstractInsnNode instruction : method.instructions) {
                if (instruction instanceof MethodInsnNode call &&
                    call.owner.equals("minecraft/portable/identity/PortableLanAutoPublishHooks") &&
                    call.name.equals("pauseButton") &&
                    call.desc.equals("(Ljava/lang/Object;Ljava/lang/Object;Ljava/util/function/Supplier;)Ljava/lang/Object;")) {
                    if (!isAlias("pauseCreateMenuMethods", method.name) || !method.desc.equals("()V")) {
                        throw new IllegalStateException("The reporting hook changed an unrelated pause method.");
                    }
                    hooks++;
                }
            }
        }
        requireHookCount(className, "pauseButton", hooks, 1);
    }

    private static void verifyLegacyPauseReportingTargets(ZipFile archive, String className, byte[] transformed)
        throws Exception {
        int index = aliasIndex("pauseScreenClasses", className);
        ClassNode widget = readClass(archive, alias("pauseLegacyWidgetClasses", index));
        requireField(widget, "pauseLegacyButtonActiveFields");
        requireMethod(widget, "pauseLegacyButtonSetMessageMethods", 1);
        ClassNode minecraft = readClass(archive, alias("minecraftClasses", index).replace('.', '/'));
        requireMethod(minecraft, "pauseLegacyLanguageManagerMethods", 0);
        ClassNode languages = readClass(archive, alias("pauseLegacyLanguageManagerClasses", index));
        requireMethod(languages, "pauseLegacyLanguageSelectedMethods", 0);
        ClassNode language = readClass(archive, alias("pauseLegacyLanguageInfoClasses", index));
        requireMethod(language, "pauseLegacyLanguageCodeMethods", 0);
        ClassNode literal = readClass(archive, alias("pauseLegacyLiteralComponentClasses", index).replace('.', '/'));
        requireConstructor(literal, "(Ljava/lang/String;)V");

        ClassNode patched = new ClassNode();
        new ClassReader(transformed).accept(patched, 0);
        int labels = 0;
        int actions = 0;
        for (MethodNode method : patched.methods) for (AbstractInsnNode instruction : method.instructions) {
            if (!(instruction instanceof MethodInsnNode call) ||
                !call.owner.equals("minecraft/portable/identity/PortableLanAutoPublishHooks")) continue;
            if (call.name.equals("decorateLegacyPauseButton") &&
                call.desc.equals("(Ljava/lang/Object;ZLjava/lang/Object;)Z") &&
                isAlias("pauseCreateMenuMethods", method.name) && method.desc.equals("()V")) labels++;
            if (call.name.equals("legacyPauseReports") &&
                call.desc.equals("(Ljava/lang/Object;)Z") &&
                method.desc.equals("(L" + alias("pauseLegacyButtonClasses", index) + ";)V")) actions++;
        }
        requireHookCount(className, "decorateLegacyPauseButton", labels, 1);
        requireHookCount(className, "legacyPauseReports", actions, 1);
    }

    private static void verifyLanPauseRowTargets(
        ZipFile archive, String className, byte[] transformed) throws Exception {
        int index = aliasIndex("pauseScreenClasses", className);
        ClassNode pause = readClass(archive, className);
        requireMethod(pause, "pauseCreateMenuMethods", 0);
        requireField(pause, "pausePlayerReportingFields");
        requireMethodDescriptor(pause, "pauseOpenScreenButtonMethods", "pauseOpenScreenButtonDescriptors");
        ClassNode rows = readClass(archive, alias("pauseRowHelperClasses", index));
        requireMethodDescriptor(rows, "pauseRowAddMethods", "pauseRowAddDescriptors");
        ClassNode widget = readClass(archive, alias("widgetClasses", index));
        requireMethod(widget, "widgetGetWidthMethods", 0);
        requireMethod(widget, "widgetSetWidthMethods", 1);
        ClassNode minecraft = readClass(archive, alias("minecraftClasses", index).replace('.', '/'));
        requireField(minecraft, "minecraftLevelFields");
        ClassNode component = readClass(archive, alias("componentClasses", index).replace('.', '/'));
        boolean translatable = component.methods.stream().anyMatch(method ->
            isAlias("componentTranslatableMethods", method.name) &&
            (method.access & Opcodes.ACC_STATIC) != 0 && method.desc.startsWith("(Ljava/lang/String;)L"));
        if (!translatable) throw new IllegalStateException("The native translated LAN caption method is missing.");
        String screen = alias("screenClasses", index).replace('.', '/');
        String level = alias("levelClasses", index).replace('.', '/');
        ClassNode worldOptions = readClass(archive, alias("worldOptionsScreenClasses", index).replace('.', '/'));
        ClassNode reporting = readClass(archive, alias("reportingScreenClasses", index).replace('.', '/'));
        requireConstructor(worldOptions, "(L" + screen + ";L" + level + ";)V");
        requireConstructor(reporting, "(L" + screen + ";)V");
        if (!"true".equals(System.getProperty("minecraft.portable.identity.lanPublishExplicitRequestOnly"))) {
            throw new IllegalStateException("The native world-options screen needs explicit-only LAN publication.");
        }
        ClassNode patched = new ClassNode();
        new ClassReader(transformed).accept(patched, 0);
        int hooks = 0;
        for (MethodNode method : patched.methods) for (AbstractInsnNode instruction : method.instructions) {
            if (instruction instanceof MethodInsnNode call &&
                call.owner.equals("minecraft/portable/identity/PortableLanAutoPublishHooks") &&
                call.name.equals("addLanPauseRow") &&
                call.desc.equals("(Ljava/lang/Object;Ljava/lang/Object;ILjava/lang/Object;)Ljava/lang/Object;")) {
                if (!isAlias("pauseCreateMenuMethods", method.name) || !method.desc.equals("()V")) {
                    throw new IllegalStateException("The explicit LAN row changed an unrelated pause method.");
                }
                hooks++;
            }
        }
        requireHookCount(className, "addLanPauseRow", hooks, 1);
    }

    private static void requireConstructor(ClassNode owner, String descriptor) {
        if (owner.methods.stream().noneMatch(method -> method.name.equals("<init>") && method.desc.equals(descriptor))) {
            throw new IllegalStateException("The native screen constructor is missing: " + owner.name + descriptor);
        }
    }

    /**
     * The one-press publish: one hook per normal init exit, and every member the hook
     * reaches for through reflection, in the mapping this class was found under.
     */
    private static void verifySteamDiscoveryTargets(ZipFile archive, String className, byte[] transformed) throws Exception {
        int index = aliasIndex("steamDiscoveryScreenClasses", className);
        ClassNode screen = readClass(archive, className);
        requireMethod(screen, "steamDiscoveryInitMethods", 0);
        requireMethod(screen, "steamDiscoveryRemovedMethods", 0);
        boolean joinFound = screen.methods.stream().anyMatch(method ->
            Arrays.asList(aliases("steamDiscoveryJoinMethods")).contains(method.name) &&
            Arrays.asList(aliases("steamDiscoveryJoinDescriptors")).contains(method.desc));
        if (!joinFound) throw new IllegalStateException("Required Steam discovery join descriptor is missing.");
        requireField(screen, "steamDiscoveryLanListFields");
        ClassNode list = readClass(archive, alias("steamDiscoveryLanListClasses", index));
        requireField(list, "steamDiscoveryLanServersFields");
        requireField(list, "steamDiscoveryLanDirtyFields");
        requireMethod(list, "steamDiscoveryLanAddMethods", 2);
        ClassNode data = readClass(archive, alias("steamDiscoveryServerDataClasses", index));
        requireField(data, "steamDiscoveryServerAddressFields");
        ClassNode lan = readClass(archive, alias("steamDiscoveryLanServerClasses", index).replace('.', '/'));
        requireMethod(lan, "steamDiscoveryLanAddressMethods", 0);
        if (lan.methods.stream().noneMatch(method -> method.name.equals("<init>") &&
            method.desc.equals("(Ljava/lang/String;Ljava/lang/String;)V")))
            throw new IllegalStateException("Unsupported LAN server entry constructor.");
        verifySteamHostDescription(archive, index);
        if (isConfigured("steamDiscoveryComponentLiteralMethods")) {
            ClassNode component = readClass(archive, alias("steamDiscoveryComponentClasses", index));
            requireMethod(component, "steamDiscoveryComponentLiteralMethods", 1);
        } else {
            ClassNode literal = readClass(archive, alias("steamDiscoveryLiteralComponentClasses", index).replace('.', '/'));
            requireConstructor(literal, "(Ljava/lang/String;)V");
        }
        ClassNode patched = new ClassNode();
        new ClassReader(transformed).accept(patched, 0);
        for (String name : new String[] { "begin", "end", "join" }) {
            int calls = 0;
            for (MethodNode method : patched.methods) for (AbstractInsnNode instruction : method.instructions)
                if (instruction instanceof MethodInsnNode call &&
                    call.owner.equals("minecraft/portable/identity/PortableSteamDiscoveryHooks") &&
                    call.name.equals(name)) calls++;
            if (calls < 1 || (!name.equals("begin") && calls != 1))
                throw new IllegalStateException("Unsupported Steam discovery hook count for " + name + ".");
        }
    }

    private static void verifySteamDiscoveryList(ZipFile archive, String name, byte[] transformed) throws Exception {
        ClassNode original = readClass(archive, name);
        requireMethod(original, "steamDiscoveryLanAddMethods", 2);
        ClassNode patched = new ClassNode(); new ClassReader(transformed).accept(patched, 0);
        long calls = patched.methods.stream().flatMap(method -> Arrays.stream(method.instructions.toArray()))
            .filter(instruction -> instruction instanceof MethodInsnNode call &&
                call.owner.equals("minecraft/portable/identity/PortableSteamDiscoveryHooks") &&
                call.name.equals("nativeDiscoveryManaged") && call.desc.equals("(Ljava/lang/Object;)Z")).count();
        if (calls != 1) throw new IllegalStateException("Automatic native LAN discovery guard count differs.");
    }

    private static void verifySteamDiscoveryEntry(byte[] transformed) {
        ClassNode patched = new ClassNode(); new ClassReader(transformed).accept(patched, 0);
        long calls = patched.methods.stream().flatMap(method -> Arrays.stream(method.instructions.toArray()))
            .filter(instruction -> instruction instanceof MethodInsnNode call &&
                call.owner.equals("minecraft/portable/identity/PortableSteamDiscoveryHooks") &&
                call.name.equals("worldTitle") && call.desc.equals("(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;")).count();
        int expected = System.getProperty("minecraft.portable.identity.steamDiscoveryLanEntryNarrationMethods", "").isBlank() ? 1 : 2;
        if (calls != expected) throw new IllegalStateException("Native Steam world header count differs.");
        long addresses = patched.methods.stream().flatMap(method -> Arrays.stream(method.instructions.toArray()))
            .filter(instruction -> instruction instanceof MethodInsnNode call &&
                call.owner.equals("minecraft/portable/identity/PortableSteamDiscoveryHooks") &&
                call.name.equals("displayAddress") && call.desc.equals("(Ljava/lang/String;Ljava/lang/Object;)Ljava/lang/String;")).count();
        if (addresses < 1 || addresses > 2) throw new IllegalStateException("Native Steam display address count differs.");
    }

    private static void verifyLanSharePublishTargets(
        ZipFile archive,
        String screenClass,
        byte[] transformed) throws Exception {
        int mappingIndex = aliasIndex("lanShareScreenClasses", screenClass);
        if (Boolean.getBoolean("minecraft.portable.identity.steamDiscoveryEnabled"))
            verifySteamHostDescription(archive, mappingIndex);
        ClassNode screen = readClass(archive, screenClass);
        requireMethod(screen, "lanShareInitMethods", 0);

        ClassNode patched = new ClassNode();
        new ClassReader(transformed).accept(patched, 0);
        int hookCalls = 0;
        int initReturns = 0;
        for (MethodNode method : patched.methods) {
            boolean init = Arrays.asList(aliases("lanShareInitMethods")).contains(method.name) && method.desc.equals("()V");
            for (var instruction = method.instructions.getFirst();
                 instruction != null;
                 instruction = instruction.getNext()) {
                if (init && instruction.getOpcode() == Opcodes.RETURN) initReturns++;
                if (instruction instanceof MethodInsnNode call &&
                    call.owner.equals("minecraft/portable/identity/PortableLanAutoPublishHooks") &&
                    call.name.equals("autoPublish") &&
                    call.desc.equals("(Ljava/lang/Object;)Z")) {
                    hookCalls++;
                }
            }
        }
        if (initReturns == 0 || hookCalls != initReturns) {
            throw new IllegalStateException(
                "LAN share screen transformer inserted " + hookCalls +
                    " publish hooks for " + initReturns + " normal init exits.");
        }

        ClassNode integrated = readClass(archive, alias("integratedServerClasses", mappingIndex));
        if (isConfigured("multiplayerScopeClasses")) {
            requireMethod(integrated, "publishServerMethods", 2);
            ClassNode scope = readClass(archive,
                alias("multiplayerScopeClasses", mappingIndex).replace('.', '/'));
            boolean lanConstantFound = false;
            for (FieldNode field : scope.fields) {
                if (isAlias("lanMultiplayerScopeFields", field.name) &&
                    (field.access & Opcodes.ACC_STATIC) != 0 &&
                    field.desc.equals("L" + scope.name + ";")) {
                    lanConstantFound = true;
                }
            }
            if (!lanConstantFound) {
                throw new IllegalStateException("Required native LAN scope constant is missing: " + scope.name);
            }
        } else {
            requireMethod(integrated, "publishServerMethods", 3);
        }
        if (isConfigured("publishServerDescriptors")) {
            requireMethodDescriptor(integrated, "publishServerMethods", "publishServerDescriptors");
        }

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
        if (isConfigured("minecraftScheduleMethods")) {
            requireInheritedMethod(archive, minecraft, "minecraftScheduleMethods", "(Ljava/lang/Runnable;)V");
        }
        requireMethod(minecraft, "updateTitleMethods", 0);
        requireField(minecraft, "minecraftGuiFields");

        ClassNode gui = readClass(archive, alias("guiClasses", mappingIndex));
        requireMethod("true".equals(System.getProperty("minecraft.portable.identity.setScreenOnGui"))
            ? gui : minecraft, "setScreenMethods", 1);
        if (isConfigured("guiHudFields")) {
            requireField(gui, "guiHudFields");
            ClassNode hud = readClass(archive, alias("hudClasses", mappingIndex));
            requireMethod(hud, "guiChatMethods", 0);
        } else {
            requireMethod(gui, "guiChatMethods", 0);
        }

        ClassNode chat = readClass(archive, alias("chatComponentClasses", mappingIndex));
        requireMethod(chat, "chatAddMessageMethods", 1);

        ClassNode publishCommand = readClass(archive, alias("publishCommandClasses", mappingIndex).replace('.', '/'));
        if (isConfigured("publishSuccessMethods")) {
            requireMethod(publishCommand, "publishSuccessMethods", 1);
        } else if (isConfigured("componentTranslatableWithArgsMethods")) {
            ClassNode component = readClass(archive, alias("componentClasses", mappingIndex).replace('.', '/'));
            if (component.methods.stream().noneMatch(method ->
                isAlias("componentTranslatableWithArgsMethods", method.name) &&
                (method.access & Opcodes.ACC_STATIC) != 0 &&
                method.desc.startsWith("(Ljava/lang/String;[Ljava/lang/Object;)L"))) {
                throw new IllegalStateException("The native translated LAN port message factory is missing.");
            }
        } else if (isConfigured("translatableComponentClasses")) {
            ClassNode translatable = readClass(archive,
                alias("translatableComponentClasses", mappingIndex).replace('.', '/'));
            requireConstructor(translatable, "(Ljava/lang/String;[Ljava/lang/Object;)V");
        }

        readClass(archive, alias("gameTypeClasses", mappingIndex).replace('.', '/'));
        readClass(archive, alias("screenClasses", mappingIndex).replace('.', '/'));
    }

    /** Host metadata is independently required even when local browser hooks are unavailable. */
    private static void verifyNativeSteamPublication(
            ZipFile archive, String className, byte[] original, byte[] transformed) throws Exception {
        int index = aliasIndex("integratedServerClasses", className);
        verifySteamHostDescription(archive, index);
        ClassNode minecraft = readClass(archive, alias("minecraftClasses", index).replace('.', '/'));
        requireSteamHostGetter(minecraft, "getSingleplayerServerMethods", "()L" + className + ";");
        ClassNode world = readClass(archive, alias("steamDiscoveryWorldDataClasses", index).replace('.', '/'));

        ClassNode before = new ClassNode();
        new ClassReader(original).accept(before, 0);
        requireInheritedMethod(archive, before, "getWorldDataMethods", "()L" + world.name + ";");
        FieldNode client = PortableSteamDiscoveryTransformer.publicationClient(before);
        MethodNode oldPublication = PortableSteamDiscoveryTransformer.publicationMethod(before);
        int portSlot = PortableSteamDiscoveryTransformer.publicationPortSlot(oldPublication);
        ClassNode after = new ClassNode();
        new ClassReader(transformed).accept(after, 0);
        MethodNode publication = PortableSteamDiscoveryTransformer.publicationMethod(after);
        if (!before.name.equals(after.name) || before.methods.size() != after.methods.size() ||
            !client.desc.equals("L" + minecraft.name + ";")) {
            throw new IllegalStateException("Unexpected native Steam publication class or client field.");
        }
        int returns = 0;
        for (AbstractInsnNode exit : publication.instructions.toArray()) {
            if (exit.getOpcode() != Opcodes.IRETURN) continue;
            if (!(exit.getPrevious() instanceof MethodInsnNode call) ||
                call.getOpcode() != Opcodes.INVOKESTATIC || call.itf ||
                !call.owner.equals("minecraft/portable/identity/PortableSteamDiscoveryHooks") ||
                !call.name.equals("publicationResult") || !call.desc.equals("(ZLjava/lang/Object;Ljava/lang/Object;I)Z") ||
                !(call.getPrevious() instanceof VarInsnNode port) || port.getOpcode() != Opcodes.ILOAD || port.var != portSlot ||
                !(port.getPrevious() instanceof VarInsnNode server) || server.getOpcode() != Opcodes.ALOAD || server.var != 0 ||
                !(server.getPrevious() instanceof FieldInsnNode field) || field.getOpcode() != Opcodes.GETFIELD ||
                !field.owner.equals(className) || !field.name.equals(client.name) || !field.desc.equals(client.desc) ||
                !(field.getPrevious() instanceof VarInsnNode self) || self.getOpcode() != Opcodes.ALOAD || self.var != 0) {
                throw new IllegalStateException("Native Steam publication return lacks its exact result hook.");
            }
            for (AbstractInsnNode added : new AbstractInsnNode[] { self, field, server, port, call })
                publication.instructions.remove(added);
            returns++;
        }
        if (returns == 0) throw new IllegalStateException("Native Steam publication has no boolean return.");
        publication.maxStack = oldPublication.maxStack;
        for (int method = 0; method < before.methods.size(); method++) {
            if (!Arrays.equals(methodBytes(before.methods.get(method)), methodBytes(after.methods.get(method))))
                throw new IllegalStateException("Native Steam publication changed existing bytecode in " +
                    before.methods.get(method).name + ".");
        }
    }

    /** Host metadata is independently required even when local browser hooks are unavailable. */
    private static void verifySteamHostDescription(ZipFile archive, int mappingIndex) throws Exception {
        ClassNode minecraft = readClass(archive, alias("minecraftClasses", mappingIndex).replace('.', '/'));
        ClassNode user = readClass(archive, alias("steamDiscoveryUserClasses", mappingIndex).replace('.', '/'));
        ClassNode world = readClass(archive, alias("steamDiscoveryWorldDataClasses", mappingIndex).replace('.', '/'));
        requireSteamHostGetter(minecraft, "steamDiscoveryGetUserMethods", "()L" + user.name + ";");
        requireSteamHostGetter(user, "steamDiscoveryUserNameMethods", "()Ljava/lang/String;");
        requireSteamHostGetter(world, "steamDiscoveryLevelNameMethods", "()Ljava/lang/String;");
    }

    private static void requireSteamHostGetter(ClassNode owner, String property, String descriptor) {
        if (owner.methods.stream().noneMatch(method -> isAlias(property, method.name) &&
            method.desc.equals(descriptor) && (method.access & Opcodes.ACC_STATIC) == 0))
            throw new IllegalStateException("Required Steam host metadata getter is missing: " + owner.name +
                " / " + property + " " + descriptor);
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

    /**
     * At least one hook in the class that was asked about, and none is the
     * failure worth catching: a patch that quietly did nothing would leave the
     * world served to the furthest asker and every other guest carrying it.
     *
     * The count is not pinned because it cannot be. ChunkMap reads its view
     * distance four times (1.17) or five (1.18 onward) in updatePlayerStatus,
     * and sixteen, eighteen or five times in move depending on the version -
     * every one of those reads becomes a call - plus one at the delivery and
     * one at the setter; ServerPlayer gets exactly one. What the check is for is the case that
     * would be silent: a patch that matched nothing and left the world served
     * to the furthest asker with everyone else carrying it.
     */
    /**
     * At least one hook went in - and where chunks are paced, exactly the seams
     * pacing needs, no more and no fewer.
     *
     * These four are counted rather than merely found. Two calls to deliverHeld
     * would quietly hand over twice the budget, none would hold every chunk
     * forever, and a shouldSend left standing beside admit would decide the
     * same question twice with the second answer thrown away.
     */
    private static void verifyPerPlayerChunkTargets(ZipFile archive, String className, byte[] transformed) throws Exception {
        ClassNode patched = new ClassNode();
        new ClassReader(transformed).accept(patched, 0);
        int hookCalls = 0;
        int admit = 0;
        int shouldSend = 0;
        int deliverHeld = 0;
        int cancelHeld = 0;
        for (MethodNode method : patched.methods) {
            for (var instruction = method.instructions.getFirst();
                 instruction != null;
                 instruction = instruction.getNext()) {
                if (!(instruction instanceof MethodInsnNode call) ||
                    !call.owner.equals("minecraft/portable/identity/PortablePerPlayerChunksHooks")) {
                    continue;
                }
                hookCalls++;
                switch (call.name) {
                    case "admit" -> admit++;
                    case "shouldSend" -> shouldSend++;
                    case "deliverHeld" -> deliverHeld++;
                    case "cancelHeld" -> cancelHeld++;
                    default -> { }
                }
            }
        }
        if (hookCalls == 0) {
            throw new IllegalStateException(
                "Per-player chunk transformer inserted no hooks into " + className + ".");
        }
        // With the narrowing off, the chunk map still gets the one seam that
        // watches how far the server is served, and none of these belong.
        if (!"true".equals(System.getProperty("minecraft.portable.identity.perPlayerChunksEnabled"))) {
            return;
        }

        boolean pacing = "true".equals(System.getProperty("minecraft.portable.identity.chunkPacingEnabled"));
        if (isAlias("chunkMapClasses", className)) {
            if (pacing && "true".equals(System.getProperty("minecraft.portable.identity.chunkBackpressureEnabled"))) {
                verifyChunkBackpressureTargets(archive, aliasIndex("chunkMapClasses", className));
            }
            requireHookCount(className, "admit", admit, pacing ? 1 : 0);
            requireHookCount(className, "shouldSend", shouldSend, pacing ? 0 : 1);
            requireHookCount(className, "deliverHeld", deliverHeld, pacing ? 1 : 0);
        } else if (isAlias("serverPlayerClasses", className)) {
            requireHookCount(className, "cancelHeld", cancelHeld, pacing ? 1 : 0);
        }
    }

    /** Check the actual native field chain; login aliases are deliberately unrelated. */
    private static void verifyChunkBackpressureTargets(ZipFile archive, int index) throws Exception {
        String playerName = alias("serverPlayerClasses", index);
        String listenerName = alias("chunkBackpressureListenerClasses", index);
        String connectionName = alias("chunkBackpressureConnectionClasses", index);
        ClassNode player = readClass(archive, playerName);
        ClassNode listener = readClass(archive, listenerName);
        ClassNode connection = readClass(archive, connectionName);
        requireInstanceFieldType(player, "chunkPlayerListenerFields", "L" + listenerName + ";");
        requireInstanceFieldType(listener, "chunkListenerConnectionFields", "L" + connectionName + ";");
        requireInstanceFieldType(connection, "chunkConnectionChannelFields", "Lio/netty/channel/Channel;");
        boolean memoryFound = connection.methods.stream().anyMatch(method ->
            isAlias("chunkMemoryConnectionMethods", method.name) && method.desc.equals("()Z") &&
            (method.access & Opcodes.ACC_STATIC) == 0);
        if (!memoryFound) throw new IllegalStateException("The native memory-connection predicate is missing.");
    }

    private static void requireInstanceFieldType(ClassNode owner, String names, String descriptor) {
        boolean found = owner.fields.stream().anyMatch(field -> isAlias(names, field.name) &&
            field.desc.equals(descriptor) && (field.access & Opcodes.ACC_STATIC) == 0);
        if (!found) throw new IllegalStateException("The native backpressure field type is missing: " + owner.name + "." + names);
    }

    /**
     * Both movement checks, and only those two.
     *
     * Counted rather than found, because the same question is asked four more
     * times in this class about things that are not a guest's to decide. One
     * call means one mode was missed. On newer clients the on-foot handler
     * delegates to a private helper; its sole caller must be that handler.
     * The total stays two, with no hooks in unrelated permission sites.
     */
    private static void verifyGuestMovementTargets(
        String className, byte[] original, byte[] transformed) throws Exception {
        ClassNode source = new ClassNode();
        new ClassReader(original).accept(source, 0);
        ClassNode patched = new ClassNode();
        new ClassReader(transformed).accept(patched, 0);
        java.util.Set<String> sites = new java.util.HashSet<>();
        for (String mode : new String[] { "Player", "Vehicle" }) {
            MethodNode handler = null;
            for (MethodNode method : source.methods) {
                if (isAlias("handleMove" + mode + "Methods", method.name) &&
                    isAlias("handleMove" + mode + "Descriptors", method.desc)) {
                    if (handler != null) throw new IllegalStateException("Ambiguous movement handler in " + className);
                    handler = method;
                }
            }
            if (handler == null) throw new IllegalStateException("Missing movement handler in " + className);
            MethodNode site = handler;
            if (mode.equals("Player") && movementOwnerChecks(handler) == 0) {
                site = null;
                if (isConfigured("movementCheckHelperMethods") && isConfigured("movementCheckHelperDescriptors")) {
                    for (MethodNode candidate : source.methods) {
                        if ((candidate.access & Opcodes.ACC_PRIVATE) == 0 ||
                            (candidate.access & Opcodes.ACC_STATIC) != 0 || !candidate.desc.equals("(Z)Z") ||
                            !isAlias("movementCheckHelperMethods", candidate.name) ||
                            !isAlias("movementCheckHelperDescriptors", candidate.desc) ||
                            movementIndirectReference(source, candidate)) continue;
                        int callers = 0;
                        boolean onlyPlayer = true;
                        for (MethodNode caller : source.methods) {
                            for (var instruction : caller.instructions) {
                                if (instruction instanceof MethodInsnNode call && call.owner.equals(source.name) &&
                                    call.name.equals(candidate.name) && call.desc.equals(candidate.desc)) {
                                    callers++;
                                    onlyPlayer &= (caller == handler || movementPositionCaller(source, caller, handler)) &&
                                        (call.getOpcode() == Opcodes.INVOKEVIRTUAL || call.getOpcode() == Opcodes.INVOKESPECIAL);
                                }
                            }
                        }
                        if (callers != 1 || !onlyPlayer || movementOwnerChecks(candidate) != 1) continue;
                        if (site != null) throw new IllegalStateException("Ambiguous movement helper in " + className);
                        site = candidate;
                    }
                }
            }
            if (site == null || movementOwnerChecks(site) != 1)
                throw new IllegalStateException("No exclusive owner check for " + mode + " movement in " + className);
            sites.add(site.name + site.desc);
        }
        int trusted = 0;
        for (MethodNode method : patched.methods) {
            int methodTrusted = 0;
            for (var instruction = method.instructions.getFirst();
                 instruction != null;
                 instruction = instruction.getNext()) {
                if (instruction instanceof MethodInsnNode call &&
                    call.owner.equals("minecraft/portable/identity/PortableGuestMovementHooks") &&
                    call.name.equals("trustedLikeTheHost")) {
                    trusted++;
                    methodTrusted++;
                }
            }
            boolean selected = sites.contains(method.name + method.desc);
            requireHookCount(className + "." + method.name + method.desc,
                "trustedLikeTheHost", methodTrusted, selected ? 1 : 0);
            if (selected && movementOwnerChecks(method) != 0)
                throw new IllegalStateException("Movement owner check was not replaced in " + className + "." + method.name);
        }
        requireHookCount(className, "trustedLikeTheHost", trusted, 2);
    }

    private static int movementOwnerChecks(MethodNode method) {
        int count = 0;
        for (var instruction : method.instructions) {
            if (instruction instanceof MethodInsnNode call && call.getOpcode() == Opcodes.INVOKEVIRTUAL &&
                call.desc.equals("()Z") && isAlias("singleplayerOwnerMethods", call.name) &&
                isAlias("singleplayerOwnerClasses", call.owner)) count++;
        }
        return count;
    }

    private static boolean movementPositionCaller(ClassNode owner, MethodNode position, MethodNode player) {
        if ((position.access & Opcodes.ACC_PRIVATE) == 0 ||
            (position.access & Opcodes.ACC_STATIC) != 0 || !position.desc.equals("(DDDFFZZ)V") ||
            !isConfigured("movementPositionMethods") || !isConfigured("movementPositionDescriptors") ||
            !isConfigured("movementTeleportAckMethods") || !isConfigured("movementTeleportAckDescriptors") ||
            !isAlias("movementPositionMethods", position.name) || !isAlias("movementPositionDescriptors", position.desc) ||
            movementIndirectReference(owner, position)) return false;
        int playerCalls = 0;
        int acknowledgementCalls = 0;
        for (MethodNode caller : owner.methods) {
            for (var instruction : caller.instructions) {
                if (!(instruction instanceof MethodInsnNode call) || !call.owner.equals(owner.name) ||
                    !call.name.equals(position.name) || !call.desc.equals(position.desc)) continue;
                if (call.getOpcode() != Opcodes.INVOKEVIRTUAL && call.getOpcode() != Opcodes.INVOKESPECIAL) return false;
                if (caller == player) playerCalls++;
                else if ((caller.access & Opcodes.ACC_STATIC) == 0 &&
                    isAlias("movementTeleportAckMethods", caller.name) && isAlias("movementTeleportAckDescriptors", caller.desc))
                    acknowledgementCalls++;
                else return false;
            }
        }
        return playerCalls == 1 && acknowledgementCalls == 1;
    }

    private static boolean movementIndirectReference(ClassNode owner, MethodNode target) {
        for (MethodNode caller : owner.methods) {
            for (var instruction : caller.instructions) {
                if (instruction instanceof org.objectweb.asm.tree.LdcInsnNode constant &&
                    movementReference(constant.cst, owner.name, target)) return true;
                if (instruction instanceof org.objectweb.asm.tree.InvokeDynamicInsnNode dynamic) {
                    if (movementReference(dynamic.bsm, owner.name, target)) return true;
                    for (Object argument : dynamic.bsmArgs) if (movementReference(argument, owner.name, target)) return true;
                }
            }
        }
        return false;
    }

    private static boolean movementReference(Object value, String owner, MethodNode target) {
        if (value instanceof org.objectweb.asm.Handle handle) return handle.getOwner().equals(owner) &&
            handle.getName().equals(target.name) && handle.getDesc().equals(target.desc);
        if (value instanceof org.objectweb.asm.ConstantDynamic dynamic) {
            if (movementReference(dynamic.getBootstrapMethod(), owner, target)) return true;
            for (int index = 0; index < dynamic.getBootstrapMethodArgumentCount(); index++)
                if (movementReference(dynamic.getBootstrapMethodArgument(index), owner, target)) return true;
        }
        return false;
    }

    private static void requireHookCount(String className, String hook, int found, int wanted) {
        if (found != wanted) {
            throw new IllegalStateException(
                "A transformer inserted " + found + " calls to " + hook + " in " + className +
                    " instead of " + wanted + ".");
        }
    }

    private static byte[] readClassBytes(ZipFile archive, String className) throws Exception {
        Path source = classSources.get(className);
        if (source != null && !source.equals(Path.of(archive.getName()).toAbsolutePath().normalize())) {
            try (ZipFile dependency = new ZipFile(source.toFile())) {
                return readExactClassBytes(dependency, className);
            }
        }
        return readExactClassBytes(archive, className);
    }

    private static byte[] readExactClassBytes(ZipFile archive, String className) throws Exception {
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

    private static void requireMethodDescriptor(
        ClassNode owner, String methodsProperty, String descriptorsProperty) {
        var names = Arrays.asList(aliases(methodsProperty));
        var descriptors = Arrays.asList(aliases(descriptorsProperty));
        for (MethodNode method : owner.methods) {
            if (names.contains(method.name) && descriptors.contains(method.desc)) return;
        }
        throw new IllegalStateException(
            "Required identity method descriptor is missing: " + owner.name + "." +
                String.join("/", names) + " " + descriptors);
    }

    private static void requireInheritedMethod(
        ZipFile archive, ClassNode owner, String propertyName, String descriptor) throws Exception {
        var names = Arrays.asList(aliases(propertyName));
        for (ClassNode current = owner; current != null;) {
            for (MethodNode method : current.methods) {
                if (names.contains(method.name) && method.desc.equals(descriptor)) return;
            }
            current = current.superName == null || current.superName.equals("java/lang/Object")
                ? null : readClass(archive, current.superName);
        }
        throw new IllegalStateException(
            "Required inherited identity method is missing: " + owner.name + "." +
                String.join("/", names) + descriptor);
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
