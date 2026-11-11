package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;
import org.objectweb.asm.tree.*;

/** Publishes and displays transient Steam worlds through native Minecraft entry points. */
public final class PortableSteamDiscoveryTransformer implements ClassFileTransformer {
    private static final String HOOKS = "minecraft/portable/identity/PortableSteamDiscoveryHooks";

    @Override
    public byte[] transform(Module module, ClassLoader loader, String name, Class<?> redefined,
                            ProtectionDomain domain, byte[] original) {
        if (!Boolean.getBoolean("minecraft.portable.identity.steamDiscoveryEnabled")) return null;
        if (isPublicationTarget(name)) return transformPublication(name, original);
        if (!Boolean.parseBoolean(System.getProperty("minecraft.portable.identity.steamDiscoveryClientEnabled", "true"))) return null;
        boolean listTarget = matches("steamDiscoveryLanListClasses", name);
        if (!listTarget && !matches("steamDiscoveryScreenClasses", name)) return null;
        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(original).accept(node, 0);
        if (listTarget) {
            int filtered = 0;
            for (MethodNode method : node.methods) {
                if (!matches("steamDiscoveryLanAddMethods", method.name) ||
                    !matches("steamDiscoveryLanAddDescriptors", method.desc)) continue;
                InsnList hook = new InsnList();
                LabelNode ordinary = new LabelNode();
                hook.add(new VarInsnNode(Opcodes.ALOAD, 0));
                hook.add(call("nativeDiscoveryManaged", "(Ljava/lang/Object;)Z"));
                hook.add(new JumpInsnNode(Opcodes.IFEQ, ordinary));
                hook.add(new InsnNode(Opcodes.RETURN));
                hook.add(ordinary);
                hook.add(new FrameNode(Opcodes.F_SAME, 0, null, 0, null));
                method.instructions.insert(hook);
                filtered++;
            }
            if (filtered != 1) throw new IllegalStateException("Unsupported native automatic LAN add method.");
            ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
            node.accept(writer);
            return writer.toByteArray();
        }
        int initialized = 0, removed = 0, joined = 0;
        for (MethodNode method : node.methods) {
            if (matches("steamDiscoveryInitMethods", method.name) && method.desc.equals("()V")) {
                // As with LAN publishing, let vanilla and mod screen widgets
                // initialize before observing the finished screen.
                for (AbstractInsnNode instruction : method.instructions.toArray()) {
                    if (instruction.getOpcode() != Opcodes.RETURN) continue;
                    InsnList hook = new InsnList();
                    hook.add(new VarInsnNode(Opcodes.ALOAD, 0));
                    hook.add(call("begin", "(Ljava/lang/Object;)V"));
                    method.instructions.insertBefore(instruction, hook);
                    initialized++;
                }
            } else if (matches("steamDiscoveryRemovedMethods", method.name) && method.desc.equals("()V")) {
                InsnList hook = new InsnList();
                hook.add(new VarInsnNode(Opcodes.ALOAD, 0));
                hook.add(call("end", "(Ljava/lang/Object;)V"));
                method.instructions.insert(hook);
                removed++;
            } else if (matches("steamDiscoveryJoinMethods", method.name) &&
                       matches("steamDiscoveryJoinDescriptors", method.desc)) {
                InsnList hook = new InsnList();
                LabelNode vanilla = new LabelNode();
                hook.add(new VarInsnNode(Opcodes.ALOAD, 0));
                hook.add(new VarInsnNode(Opcodes.ALOAD, 1));
                hook.add(call("join", "(Ljava/lang/Object;Ljava/lang/Object;)Z"));
                hook.add(new JumpInsnNode(Opcodes.IFEQ, vanilla));
                hook.add(new InsnNode(Opcodes.RETURN));
                hook.add(vanilla);
                hook.add(new FrameNode(Opcodes.F_SAME, 0, null, 0, null));
                method.instructions.insert(hook);
                joined++;
            }
        }
        if (initialized == 0 || removed != 1 || joined != 1)
            throw new IllegalStateException("Unsupported multiplayer screen for Steam discovery.");
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched multiplayer Steam discovery.");
        return writer.toByteArray();
    }

    private static MethodInsnNode call(String name, String descriptor) {
        return new MethodInsnNode(Opcodes.INVOKESTATIC, HOOKS, name, descriptor, false);
    }

    static boolean isPublicationTarget(String name) {
        return Boolean.getBoolean("minecraft.portable.identity.steamNativePublicationEnabled") &&
            matches("integratedServerClasses", name);
    }

    private static byte[] transformPublication(String name, byte[] original) {
        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(original).accept(node, 0);
        FieldNode client = publicationClient(node);
        MethodNode publication = publicationMethod(node);
        if (!name.equals(node.name)) throw new IllegalStateException("Unexpected native Steam publication class.");
        int returns = 0;
        for (AbstractInsnNode instruction : publication.instructions.toArray()) {
            if (instruction.getOpcode() != Opcodes.IRETURN) continue;
            InsnList hook = new InsnList();
            // The native boolean stays on the stack. The hook returns that same
            // value so early refusals and failed binds keep their native result.
            hook.add(new VarInsnNode(Opcodes.ALOAD, 0));
            hook.add(new FieldInsnNode(Opcodes.GETFIELD, node.name, client.name, client.desc));
            hook.add(new VarInsnNode(Opcodes.ALOAD, 0));
            hook.add(new VarInsnNode(Opcodes.ILOAD, 2));
            hook.add(call("publicationResult", "(ZLjava/lang/Object;Ljava/lang/Object;I)Z"));
            publication.instructions.insertBefore(instruction, hook);
            returns++;
        }
        if (returns == 0) throw new IllegalStateException("Native Steam publication has no boolean return.");
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        return writer.toByteArray();
    }

    static FieldNode publicationClient(ClassNode node) {
        var matches = node.fields.stream().filter(field ->
            (field.access & Opcodes.ACC_STATIC) == 0 &&
            matches("steamNativePublicationClientFields", field.name) &&
            matchesClass("minecraftClasses", Type.getType(field.desc))).toList();
        if (matches.size() != 1)
            throw new IllegalStateException("Expected one mapped native Steam publication client field in " + node.name + ".");
        return matches.get(0);
    }

    static MethodNode publicationMethod(ClassNode node) {
        var matches = node.methods.stream().filter(method ->
            (method.access & (Opcodes.ACC_STATIC | Opcodes.ACC_ABSTRACT | Opcodes.ACC_NATIVE)) == 0 &&
            matches("publishServerMethods", method.name) &&
            matches("publishServerDescriptors", method.desc)).toList();
        if (matches.size() != 1)
            throw new IllegalStateException("Expected one mapped native Steam publication method in " + node.name + ".");
        MethodNode method = matches.get(0);
        Type[] parameters = Type.getArgumentTypes(method.desc);
        if (parameters.length != 2 || !matchesClass("multiplayerScopeClasses", parameters[0]) ||
            !parameters[1].equals(Type.INT_TYPE) || !Type.getReturnType(method.desc).equals(Type.BOOLEAN_TYPE))
            throw new IllegalStateException("Unexpected native Steam publication signature in " + node.name + ".");
        return method;
    }

    private static boolean matchesClass(String property, Type type) {
        return type.getSort() == Type.OBJECT &&
            (matches(property, type.getInternalName()) || matches(property, type.getClassName()));
    }

    private static boolean matches(String property, String value) {
        for (String candidate : System.getProperty("minecraft.portable.identity." + property, "").split(","))
            if (candidate.trim().equals(value)) return true;
        return false;
    }
}
