package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import java.util.*;
import org.objectweb.asm.*;
import org.objectweb.asm.tree.*;

/** Replaces only the mapped native LAN row header, keeping every other draw and native row. */
public final class PortableSteamLanEntryTransformer implements ClassFileTransformer {
    private static final String HOOKS = "minecraft/portable/identity/PortableSteamDiscoveryHooks";
    private static final String HOOK = "worldTitle";
    private static final String HOOK_DESCRIPTOR = "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;";
    private static final String ADDRESS_HOOK = "displayAddress";
    private static final String ADDRESS_DESCRIPTOR = "(Ljava/lang/String;Ljava/lang/Object;)Ljava/lang/String;";

    @Override
    public byte[] transform(Module module, ClassLoader loader, String name, Class<?> redefined,
                            ProtectionDomain domain, byte[] original) {
        if (!Boolean.getBoolean("minecraft.portable.identity.steamDiscoveryEnabled") ||
            !Boolean.parseBoolean(System.getProperty("minecraft.portable.identity.steamDiscoveryClientEnabled", "true")) ||
            !matches("steamDiscoveryLanEntryClasses", name)) return null;
        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(original).accept(node, 0);
        FieldNode header = field(node, "steamDiscoveryLanEntryHeaderFields", true);
        FieldNode server = field(node, "steamDiscoveryLanEntryServerFields", false);
        List<FieldInsnNode> reads = new ArrayList<>();
        MethodNode render = collect(node, header, "steamDiscoveryLanEntryRenderMethods", "V", reads, true);
        MethodNode narration = collect(node, header, "steamDiscoveryLanEntryNarrationMethods", header.desc, reads,
            !aliases("steamDiscoveryLanEntryNarrationMethods").isEmpty());
        List<MethodInsnNode> addresses = new ArrayList<>();
        collectAddresses(render, server, addresses, true);
        if (narration != null) collectAddresses(narration, server, addresses, false);

        int hooks = 0, wrapped = 0;
        for (MethodNode method : node.methods) for (AbstractInsnNode instruction : method.instructions)
            if (instruction instanceof MethodInsnNode call && call.owner.equals(HOOKS) &&
                (call.name.equals(HOOK) || call.name.equals(ADDRESS_HOOK))) hooks++;
        for (FieldInsnNode read : reads) if (wrapped(read, node.name, server, header.desc)) wrapped++;
        for (MethodInsnNode address : addresses)
            if (wrappedAddress(address, node.name, server)) wrapped++;
        int expected = reads.size() + addresses.size();
        if (hooks != 0 || wrapped != 0) {
            if (hooks == expected && wrapped == expected) return null;
            throw new IllegalStateException("Partially patched native Steam LAN entry labels.");
        }

        for (FieldInsnNode read : reads) {
            InsnList replacement = new InsnList();
            replacement.add(new VarInsnNode(Opcodes.ALOAD, 0));
            replacement.add(new FieldInsnNode(Opcodes.GETFIELD, node.name, server.name, server.desc));
            replacement.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HOOKS, HOOK, HOOK_DESCRIPTOR, false));
            replacement.add(new TypeInsnNode(Opcodes.CHECKCAST, Type.getType(header.desc).getInternalName()));
            for (MethodNode method : node.methods) if (method.instructions.contains(read)) {
                method.instructions.insert(read, replacement);
                break;
            }
        }
        for (MethodInsnNode address : addresses) {
            InsnList replacement = new InsnList();
            replacement.add(new VarInsnNode(Opcodes.ALOAD, 0));
            replacement.add(new FieldInsnNode(Opcodes.GETFIELD, node.name, server.name, server.desc));
            replacement.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HOOKS, ADDRESS_HOOK, ADDRESS_DESCRIPTOR, false));
            for (MethodNode method : node.methods) if (method.instructions.contains(address)) {
                method.instructions.insert(address, replacement);
                break;
            }
        }
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched native Steam LAN world labels.");
        return writer.toByteArray();
    }

    private static FieldNode field(ClassNode owner, String property, boolean isStatic) {
        List<FieldNode> fields = owner.fields.stream().filter(field -> matches(property, field.name)).toList();
        if (fields.size() != 1) throw new IllegalStateException("Missing or ambiguous native LAN entry field: " + property);
        FieldNode field = fields.get(0);
        if (((field.access & Opcodes.ACC_STATIC) != 0) != isStatic ||
            Type.getType(field.desc).getSort() != Type.OBJECT)
            throw new IllegalStateException("Unsupported native LAN entry field descriptor: " + property);
        return field;
    }

    private static MethodNode collect(ClassNode owner, FieldNode header, String property, String result,
                                List<FieldInsnNode> reads, boolean required) {
        if (!required) return null;
        List<MethodNode> methods = owner.methods.stream().filter(method ->
            matches(property, method.name) && (method.access & Opcodes.ACC_STATIC) == 0 &&
            Type.getReturnType(method.desc).getDescriptor().equals(result)).toList();
        if (methods.size() != 1) throw new IllegalStateException("Missing or ambiguous native LAN header method: " + property);
        int before = reads.size();
        for (AbstractInsnNode instruction : methods.get(0).instructions)
            if (instruction instanceof FieldInsnNode read && read.getOpcode() == Opcodes.GETSTATIC &&
                read.owner.equals(owner.name) && read.name.equals(header.name) && read.desc.equals(header.desc)) reads.add(read);
        if (reads.size() != before + 1) throw new IllegalStateException("Native LAN header method requires one exact header read: " + property);
        return methods.get(0);
    }

    private static void collectAddresses(MethodNode method, FieldNode server, List<MethodInsnNode> addresses, boolean required) {
        String owner = Type.getType(server.desc).getInternalName();
        if (!aliases("steamDiscoveryLanServerClasses").stream().map(name -> name.replace('.', '/')).anyMatch(owner::equals))
            throw new IllegalStateException("Native LAN entry server field has an unreviewed class.");
        int before = addresses.size();
        for (AbstractInsnNode instruction : method.instructions)
            if (instruction instanceof MethodInsnNode call && call.getOpcode() == Opcodes.INVOKEVIRTUAL &&
                call.owner.equals(owner) && matches("steamDiscoveryLanAddressMethods", call.name) &&
                call.desc.equals("()Ljava/lang/String;")) addresses.add(call);
        int count = addresses.size() - before;
        if ((required && count != 1) || (!required && count > 1))
            throw new IllegalStateException("Native LAN entry requires one render address site and at most one narration site.");
    }

    private static boolean wrapped(FieldInsnNode read, String owner, FieldNode server, String component) {
        AbstractInsnNode instruction = next(read);
        if (!(instruction instanceof VarInsnNode load) || load.getOpcode() != Opcodes.ALOAD || load.var != 0) return false;
        instruction = next(instruction);
        if (!(instruction instanceof FieldInsnNode field) || field.getOpcode() != Opcodes.GETFIELD ||
            !field.owner.equals(owner) || !field.name.equals(server.name) || !field.desc.equals(server.desc)) return false;
        instruction = next(instruction);
        if (!(instruction instanceof MethodInsnNode call) || call.getOpcode() != Opcodes.INVOKESTATIC ||
            !call.owner.equals(HOOKS) || !call.name.equals(HOOK) || !call.desc.equals(HOOK_DESCRIPTOR)) return false;
        instruction = next(instruction);
        return instruction instanceof TypeInsnNode cast && cast.getOpcode() == Opcodes.CHECKCAST &&
            cast.desc.equals(Type.getType(component).getInternalName());
    }

    private static boolean wrappedAddress(MethodInsnNode read, String owner, FieldNode server) {
        AbstractInsnNode instruction = next(read);
        if (!(instruction instanceof VarInsnNode load) || load.getOpcode() != Opcodes.ALOAD || load.var != 0) return false;
        instruction = next(instruction);
        if (!(instruction instanceof FieldInsnNode field) || field.getOpcode() != Opcodes.GETFIELD ||
            !field.owner.equals(owner) || !field.name.equals(server.name) || !field.desc.equals(server.desc)) return false;
        instruction = next(instruction);
        return instruction instanceof MethodInsnNode call && call.getOpcode() == Opcodes.INVOKESTATIC &&
            call.owner.equals(HOOKS) && call.name.equals(ADDRESS_HOOK) && call.desc.equals(ADDRESS_DESCRIPTOR);
    }

    private static AbstractInsnNode next(AbstractInsnNode instruction) {
        do { instruction = instruction.getNext(); } while (instruction != null && instruction.getOpcode() < 0);
        return instruction;
    }

    private static boolean matches(String property, String value) { return aliases(property).contains(value); }
    private static Set<String> aliases(String property) {
        var result = new LinkedHashSet<String>();
        for (String name : System.getProperty("minecraft.portable.identity." + property, "").split(","))
            if (!name.isBlank()) result.add(name.trim());
        return result;
    }
}
