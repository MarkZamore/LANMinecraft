package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.AnnotationNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.FieldInsnNode;
import org.objectweb.asm.tree.InsnList;
import org.objectweb.asm.tree.InsnNode;
import org.objectweb.asm.tree.JumpInsnNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;
import org.objectweb.asm.tree.TypeInsnNode;
import org.objectweb.asm.tree.VarInsnNode;

public final class PortableLanAutoPublishTransformer implements ClassFileTransformer {
    private static final String HOOKS =
        "minecraft/portable/identity/PortableLanAutoPublishHooks";

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        // The class list is named even when the hook is off, because the
        // preflight asks for it by name before it looks at anything else. What
        // decides whether anything is patched is this flag: a Minecraft missing
        // one of the names the hook reaches for keeps its skins and its UUID
        // and simply does not get the one-press publish.
        if (!"true".equals(property("lanPublishEnabled", "false"))) {
            return null;
        }
        if (("true".equals(property("pauseReportingEnabled", "false")) ||
            "true".equals(property("pauseLanRowEnabled", "false"))) &&
            contains(property("pauseScreenClasses", ""), className)) {
            return "true".equals(property("pauseLanRowEnabled", "false"))
                ? transformPauseRow(className, classfileBuffer)
                : transformPauseMenu(className, classfileBuffer);
        }
        if (!contains(property("lanShareScreenClasses", ""), className)) {
            return null;
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);
        preserveNativeInit(node);

        boolean patched = false;
        String initMethods = property("lanShareInitMethods", "init,aT_");
        for (MethodNode method : node.methods) {
            if (!contains(initMethods, method.name) || !method.desc.equals("()V")) {
                continue;
            }

            // The screen builds itself first and the publish comes after it,
            // rather than instead of it. Mods are handed the finished screen
            // the moment init returns and they read the buttons it made: LAN
            // Server Properties looks for the start button by name and dies on
            // the null when it is not there, which is what a skipped body left
            // behind - the world opened and the game came down with it.
            // Every normal exit matters: a native or modded init can finish
            // through more than one branch. Exceptions keep their original
            // path and never publish a partly initialized screen.
            for (AbstractInsnNode exit : method.instructions.toArray()) {
                if (exit.getOpcode() != Opcodes.RETURN) {
                    continue;
                }
                InsnList publish = new InsnList();
                publish.add(new VarInsnNode(Opcodes.ALOAD, 0));
                publish.add(new MethodInsnNode(
                    Opcodes.INVOKESTATIC,
                    HOOKS,
                    "autoPublish",
                    "(Ljava/lang/Object;)Z",
                    false));
                publish.add(new InsnNode(Opcodes.POP));
                method.instructions.insertBefore(exit, publish);
                patched = true;
            }
        }

        if (!patched) {
            // The JVM swallows transformer failures, so without this line the
            // vanilla settings screen would come back with no explanation.
            System.err.println(
                "[PortableIdentity] LAN share screen patch failed for " + className + ".");
            throw unsupported("share screen init was not found");
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched LAN share screen class " + className + ".");
        return writer.toByteArray();
    }

    private static void preserveNativeInit(ClassNode node) {
        if (!"true".equals(property("lanRedirectNativeInitEnabled", "false"))) return;
        String sha256 = property("lanRedirectTransportSha256", "");
        String origin = property("lanRedirectMixinOrigin", "");
        Set<String> supported = Set.of(
            "link.e4steam.mixin.ShareToLanScreenMixin",
            "link.e4steam.mixin.MultiplayerOptionsScreenMixin",
            "link.e4steam.mixin.WorldOptionsScreenMixin");
        if (!sha256.matches("[0-9a-f]{64}") || !supported.contains(origin)) {
            throw unsupported("the native-init redirect compatibility lacks its verified transport pin");
        }
        Set<String> redirects = new HashSet<>();
        for (MethodNode method : node.methods) {
            if (!method.desc.equals("(Lorg/spongepowered/asm/mixin/injection/callback/CallbackInfo;)V") ||
                (method.access & Opcodes.ACC_STATIC) != 0 || !origin.equals(mergedOrigin(method))) continue;
            List<AbstractInsnNode> body = executable(method);
            boolean redirect = body.stream().anyMatch(instruction ->
                instruction instanceof TypeInsnNode type && type.getOpcode() == Opcodes.NEW &&
                    type.desc.equals("link/e4steam/lan/LanSettingsScreen") ||
                instruction instanceof MethodInsnNode call &&
                    ((call.owner.equals("org/spongepowered/asm/mixin/injection/callback/CallbackInfo") &&
                        call.name.equals("cancel")) ||
                     (call.owner.equals("link/e4steam/MinecraftUiCompat") && call.name.equals("setScreen"))));
            if (!redirect) continue;
            if (!verifiedRedirectBody(node.name, origin, body)) {
                throw unsupported("the pinned e4steam redirect handler has an unexpected body");
            }
            redirects.add(method.name);
        }
        if (redirects.isEmpty()) return; // Raw native JARs have not been through Mixin yet.
        if (redirects.size() != 1) throw unsupported("more than one pinned redirect handler was merged");
        int calls = 0;
        for (MethodNode method : node.methods) {
            if (!contains(property("lanShareInitMethods", "init,aT_"), method.name) ||
                !method.desc.equals("()V") || (method.access & Opcodes.ACC_STATIC) != 0) continue;
            for (AbstractInsnNode instruction : method.instructions.toArray()) {
                if (!(instruction instanceof MethodInsnNode call) || !call.owner.equals(node.name) ||
                    !redirects.contains(call.name)) continue;
                if (call.itf || (call.getOpcode() != Opcodes.INVOKEVIRTUAL &&
                    call.getOpcode() != Opcodes.INVOKESPECIAL) ||
                    !call.desc.equals("(Lorg/spongepowered/asm/mixin/injection/callback/CallbackInfo;)V")) {
                    throw unsupported("the pinned redirect is not a void instance CallbackInfo call");
                }
                // This and CallbackInfo are still consumed, but the one exact
                // form-redirect callback cannot cancel native initialization.
                // Its cancellation test and every other callback stay intact.
                method.instructions.set(call, new InsnNode(Opcodes.POP2));
                calls++;
            }
        }
        if (calls != 1) throw unsupported("expected one pinned redirect init call, found " + calls);
        System.out.println("[PortableIdentity] Preserved native LAN init around the pinned e4steam 0.3.4 form redirect.");
    }

    private static String mergedOrigin(MethodNode method) {
        for (List<AnnotationNode> annotations : Arrays.asList(method.visibleAnnotations, method.invisibleAnnotations)) {
            if (annotations == null) continue;
            for (AnnotationNode annotation : annotations) {
                if (!annotation.desc.equals("Lorg/spongepowered/asm/mixin/transformer/meta/MixinMerged;") ||
                    annotation.values == null) continue;
                for (int index = 0; index + 1 < annotation.values.size(); index += 2) {
                    if (annotation.values.get(index).equals("mixin") && annotation.values.get(index + 1) instanceof String value)
                        return value;
                }
            }
        }
        return null;
    }

    private static List<AbstractInsnNode> executable(MethodNode method) {
        List<AbstractInsnNode> body = new ArrayList<>();
        for (AbstractInsnNode instruction : method.instructions) if (instruction.getOpcode() >= 0) body.add(instruction);
        return body;
    }

    private static boolean verifiedRedirectBody(String owner, String origin, List<AbstractInsnNode> body) {
        boolean world = origin.equals("link.e4steam.mixin.WorldOptionsScreenMixin");
        int[] shape = world
            ? new int[] {
                Opcodes.ALOAD, Opcodes.GETFIELD, Opcodes.INVOKEVIRTUAL, Opcodes.IFNONNULL, Opcodes.RETURN,
                Opcodes.ALOAD, Opcodes.INVOKESTATIC, Opcodes.IFEQ, Opcodes.ALOAD, Opcodes.ICONST_1,
                Opcodes.PUTFIELD, Opcodes.RETURN, Opcodes.ALOAD, Opcodes.INVOKEVIRTUAL, Opcodes.ALOAD,
                Opcodes.GETFIELD, Opcodes.NEW, Opcodes.DUP, Opcodes.ALOAD, Opcodes.GETFIELD, Opcodes.ALOAD,
                Opcodes.INVOKESPECIAL, Opcodes.INVOKESTATIC, Opcodes.RETURN }
            : new int[] {
                Opcodes.INVOKESTATIC, Opcodes.ASTORE, Opcodes.ALOAD, Opcodes.INVOKEVIRTUAL, Opcodes.IFNONNULL,
                Opcodes.RETURN, Opcodes.ALOAD, Opcodes.INVOKEVIRTUAL, Opcodes.ALOAD, Opcodes.NEW,
                Opcodes.DUP, Opcodes.ACONST_NULL, Opcodes.INVOKESPECIAL, Opcodes.INVOKESTATIC, Opcodes.RETURN };
        if (body.size() != shape.length) return false;
        for (int index = 0; index < shape.length; index++) if (body.get(index).getOpcode() != shape[index]) return false;
        if (world) {
            if (!variable(body, 0, 0) || !clientField(body.get(1), owner) ||
                !clientCall(body.get(2), "getSingleplayerServerMethods") || !jumpsTo(body, 3, 5) ||
                !variable(body, 5, 0) || !call(body.get(6), "link/e4steam/lan/WorldOptionsScreenSupport",
                    "consumeNativeWorldTab", "screenClasses", 1, "Z") || !jumpsTo(body, 7, 12) ||
                !variable(body, 8, 0) || !(body.get(10) instanceof FieldInsnNode flag) ||
                !flag.owner.equals(owner) || !flag.name.equals("e4steam$showWorldTab") || !flag.desc.equals("Z") ||
                !variable(body, 12, 1) || !variable(body, 14, 0) || !clientField(body.get(15), owner) ||
                !variable(body, 18, 0) || !(body.get(19) instanceof FieldInsnNode parent) ||
                !parent.owner.equals(owner) || !classDescriptor("screenClasses", parent.desc) ||
                !variable(body, 20, 0)) return false;
        } else if (!clientCall(body.get(0), "minecraftGetInstanceMethods") || !variable(body, 1, 2) ||
            !variable(body, 2, 2) || !clientCall(body.get(3), "getSingleplayerServerMethods") ||
            !jumpsTo(body, 4, 6) || !variable(body, 6, 1) || !variable(body, 8, 2)) return false;
        int cancel = world ? 13 : 7;
        int allocation = world ? 16 : 9;
        int constructor = world ? 21 : 12;
        int changeScreen = world ? 22 : 13;
        return body.get(cancel) instanceof MethodInsnNode cancellation && !cancellation.itf &&
            cancellation.owner.equals("org/spongepowered/asm/mixin/injection/callback/CallbackInfo") &&
            cancellation.name.equals("cancel") && cancellation.desc.equals("()V") &&
            body.get(allocation) instanceof TypeInsnNode type && type.desc.equals("link/e4steam/lan/LanSettingsScreen") &&
            call(body.get(constructor), "link/e4steam/lan/LanSettingsScreen", "<init>", "screenClasses", world ? 2 : 1, "V") &&
            body.get(changeScreen) instanceof MethodInsnNode change && !change.itf &&
            change.owner.equals("link/e4steam/MinecraftUiCompat") && change.name.equals("setScreen") &&
            Type.getReturnType(change.desc).equals(Type.VOID_TYPE) && Type.getArgumentTypes(change.desc).length == 2 &&
            classDescriptor("minecraftClasses", Type.getArgumentTypes(change.desc)[0].getDescriptor()) &&
            classDescriptor("screenClasses", Type.getArgumentTypes(change.desc)[1].getDescriptor());
    }

    private static boolean variable(List<AbstractInsnNode> body, int index, int slot) {
        return body.get(index) instanceof VarInsnNode variable && variable.var == slot;
    }

    private static boolean jumpsTo(List<AbstractInsnNode> body, int from, int to) {
        if (!(body.get(from) instanceof JumpInsnNode jump)) return false;
        AbstractInsnNode target = jump.label;
        while (target != null && target.getOpcode() < 0) target = target.getNext();
        return target == body.get(to);
    }

    private static boolean clientField(AbstractInsnNode instruction, String owner) {
        return instruction instanceof FieldInsnNode field && field.owner.equals(owner) &&
            classDescriptor("minecraftClasses", field.desc);
    }

    private static boolean clientCall(AbstractInsnNode instruction, String methods) {
        return instruction instanceof MethodInsnNode call && !call.itf &&
            contains(property(methods, ""), call.name) && className("minecraftClasses", call.owner) &&
            Type.getArgumentTypes(call.desc).length == 0 && Type.getReturnType(call.desc).getSort() == Type.OBJECT;
    }

    private static boolean call(AbstractInsnNode instruction, String owner, String name,
        String parameterClasses, int count, String result) {
        if (!(instruction instanceof MethodInsnNode call) || call.itf || !call.owner.equals(owner) ||
            !call.name.equals(name) || !Type.getReturnType(call.desc).getDescriptor().equals(result)) return false;
        Type[] parameters = Type.getArgumentTypes(call.desc);
        if (parameters.length != count) return false;
        for (Type parameter : parameters) if (!classDescriptor(parameterClasses, parameter.getDescriptor())) return false;
        return true;
    }

    private static boolean classDescriptor(String classes, String descriptor) {
        return descriptor.startsWith("L") && descriptor.endsWith(";") &&
            className(classes, descriptor.substring(1, descriptor.length() - 1));
    }

    private static boolean className(String classes, String name) {
        for (String candidate : property(classes, "").split(",")) {
            if (candidate.trim().replace('.', '/').equals(name)) return true;
        }
        return false;
    }

    private static byte[] transformPauseMenu(String className, byte[] original) {
        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(original).accept(node, 0);
        int patched = 0;
        for (MethodNode method : node.methods) {
            if (!contains(property("pauseCreateMenuMethods", ""), method.name) ||
                !method.desc.equals("()V")) continue;
            boolean multiplayerButton = false;
            for (AbstractInsnNode instruction : method.instructions.toArray()) {
                if (instruction instanceof FieldInsnNode field && field.getOpcode() == Opcodes.GETSTATIC &&
                    field.owner.equals(node.name)) {
                    multiplayerButton = contains(property("pauseMultiplayerFields", ""), field.name);
                }
                if (!multiplayerButton || !(instruction instanceof MethodInsnNode call) ||
                    call.getOpcode() != Opcodes.INVOKEVIRTUAL || call.itf ||
                    !call.owner.equals(node.name) ||
                    !contains(property("pauseOpenScreenButtonMethods", ""), call.name) ||
                    !contains(property("pauseOpenScreenButtonDescriptors", ""), call.desc)) continue;

                // Keep the native button builder and its normal screen-opening
                // lifecycle. Only this multiplayer slot chooses its component
                // and native screen from the integrated server's actual state.
                MethodInsnNode replacement = new MethodInsnNode(
                    Opcodes.INVOKESTATIC, HOOKS, "pauseButton",
                    "(Ljava/lang/Object;Ljava/lang/Object;Ljava/util/function/Supplier;)Ljava/lang/Object;", false);
                method.instructions.set(call, replacement);
                method.instructions.insert(replacement, new TypeInsnNode(
                    Opcodes.CHECKCAST, Type.getReturnType(call.desc).getInternalName()));
                multiplayerButton = false;
                patched++;
            }
        }
        if (patched != 1) {
            throw unsupported("expected one native multiplayer pause button in " + className + ", found " + patched);
        }
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched published-world pause button class " + className + ".");
        return writer.toByteArray();
    }

    private static byte[] transformPauseRow(String className, byte[] original) {
        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(original).accept(node, 0);
        int patched = 0;
        for (MethodNode method : node.methods) {
            if (!contains(property("pauseCreateMenuMethods", ""), method.name) ||
                !method.desc.equals("()V") || (method.access & Opcodes.ACC_STATIC) != 0) continue;
            for (AbstractInsnNode instruction : method.instructions.toArray()) {
                if (!(instruction instanceof MethodInsnNode call) ||
                    call.getOpcode() != Opcodes.INVOKEVIRTUAL || call.itf ||
                    !contains(property("pauseRowHelperClasses", ""), call.owner) ||
                    !contains(property("pauseRowAddMethods", ""), call.name) ||
                    !contains(property("pauseRowAddDescriptors", ""), call.desc)) continue;
                // The native disconnect row supplies the real width and column
                // span. Its original insertion still runs after the LAN row;
                // the separate world-options entry is never intercepted.
                method.instructions.insertBefore(call, new VarInsnNode(Opcodes.ALOAD, 0));
                MethodInsnNode replacement = new MethodInsnNode(
                    Opcodes.INVOKESTATIC, HOOKS, "addLanPauseRow",
                    "(Ljava/lang/Object;Ljava/lang/Object;ILjava/lang/Object;)Ljava/lang/Object;", false);
                method.instructions.set(call, replacement);
                method.instructions.insert(replacement, new TypeInsnNode(
                    Opcodes.CHECKCAST, Type.getReturnType(call.desc).getInternalName()));
                patched++;
            }
        }
        if (patched != 1) {
            throw unsupported("expected one native full-width disconnect row in " + className + ", found " + patched);
        }
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched native LAN/reporting pause row class " + className + ".");
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

    private static IllegalStateException unsupported(String detail) {
        return new IllegalStateException("Unsupported Minecraft LAN share screen bytecode: " + detail + ".");
    }
}
