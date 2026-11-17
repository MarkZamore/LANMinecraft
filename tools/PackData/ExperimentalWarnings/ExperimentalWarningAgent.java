package lanmc.experiment;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.security.ProtectionDomain;
import java.nio.file.*;
import java.util.*;
import java.util.zip.*;
import org.objectweb.asm.*;
import org.objectweb.asm.tree.*;

/** Pack-owned presentation policy. No world data or lifecycle is written. */
public final class ExperimentalWarningAgent implements ClassFileTransformer {
    private static final String BACKUP = "selectWorld.backupQuestion.experimental";
    private static final Set<String> CREATION = new HashSet<String>(Arrays.asList(
        "selectWorld.warning.experimental.title",
        "selectWorld.import_worldgen_settings.experimental.title"));
    public static void premain(String argument, Instrumentation instrumentation) {
        instrumentation.addTransformer(new ExperimentalWarningAgent());
    }
    public byte[] transform(ClassLoader loader, String name, Class<?> redefining,
                            ProtectionDomain domain, byte[] bytes) {
        if (name == null || !(name.startsWith("net/minecraft/") || name.indexOf('/') < 0)) return null;
        try { return patch(bytes); }
        catch (RuntimeException error) {
            System.err.println("[Experimental world confirmation] Could not inspect " + name + ": " + error);
            return null;
        }
    }
    public static byte[] patch(byte[] bytes) {
        String constants = new String(bytes, java.nio.charset.StandardCharsets.ISO_8859_1);
        if (!constants.contains(BACKUP) && !constants.contains("selectWorld.warning.experimental.title")
                && !constants.contains("selectWorld.import_worldgen_settings.experimental.title")) return null;
        ClassNode type = new ClassNode();
        new ClassReader(bytes).accept(type, 0);
        int changed = 0;
        for (MethodNode method : type.methods) {
            Set<String> strings = new HashSet<String>();
            for (AbstractInsnNode instruction : method.instructions.toArray())
                if (instruction instanceof LdcInsnNode && ((LdcInsnNode) instruction).cst instanceof String)
                    strings.add((String) ((LdcInsnNode) instruction).cst);
            boolean creation = !Collections.disjoint(strings, CREATION);
            boolean backup = strings.contains(BACKUP);
            if (!creation && !backup) continue;
            Type[] arguments = Type.getArgumentTypes(method.desc);
            if (Type.getReturnType(method.desc).getSort() != Type.VOID) continue;
            int slot = (method.access & Opcodes.ACC_STATIC) == 0 ? 1 : 0;
            int lifecycle = -1, runnable = -1, bool = -1, runnableCount = 0, boolCount = 0;
            for (Type arg : arguments) {
                if (arg.getDescriptor().equals("Lcom/mojang/serialization/Lifecycle;")) lifecycle = slot;
                if (arg.getDescriptor().equals("Ljava/lang/Runnable;")) {
                    if (runnable < 0) runnable = slot;
                    runnableCount++;
                }
                if (arg.getSort() == Type.BOOLEAN) { bool = slot; boolCount++; }
                slot += arg.getSize();
            }
            InsnList prefix = new InsnList();
            LabelNode original = new LabelNode();
            if (creation && lifecycle >= 0) {
                allowExperimentalCreation(method, lifecycle);
                changed++;
                continue;
            } else if (backup && !creation && lifecycle < 0 && boolCount == 1 && runnableCount >= 1
                    && runnableCount <= 2 && (method.access & Opcodes.ACC_STATIC) == 0) {
                prefix.add(new VarInsnNode(Opcodes.ILOAD, bool));
                prefix.add(new JumpInsnNode(Opcodes.IFNE, original));
            } else {
                throw new IllegalArgumentException("Unreviewed experimental prompt signature: " + type.name + "." + method.name + method.desc);
            }
            prefix.add(new VarInsnNode(Opcodes.ALOAD, runnable));
            prefix.add(new MethodInsnNode(Opcodes.INVOKEINTERFACE, "java/lang/Runnable", "run", "()V", true));
            prefix.add(new InsnNode(Opcodes.RETURN));
            prefix.add(original);
            prefix.add(new FrameNode(Opcodes.F_SAME, 0, null, 0, null));
            method.instructions.insert(prefix);
            changed++;
        }
        if (changed == 0) return null;
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        type.accept(writer);
        return writer.toByteArray();
    }
    private static void allowExperimentalCreation(MethodNode method, int lifecycle) {
        int changed = 0;
        for (AbstractInsnNode instruction : method.instructions.toArray()) {
            if (!(instruction instanceof MethodInsnNode)) continue;
            MethodInsnNode call = (MethodInsnNode) instruction;
            if (!call.owner.equals("com/mojang/serialization/Lifecycle") || !call.name.equals("stable")) continue;
            AbstractInsnNode next = call.getNext();
            while (next != null && next.getOpcode() < 0) next = next.getNext();
            if (!(next instanceof JumpInsnNode) || next.getOpcode() != Opcodes.IF_ACMPNE) continue;
            JumpInsnNode branch = (JumpInsnNode) next;
            InsnList approved = new InsnList();
            // Preserve the existing stable continuation and every deprecated/error path.
            // Objects.equals is identity equality for DFU Lifecycle and is available on Java 8.
            approved.add(new MethodInsnNode(Opcodes.INVOKESTATIC, "java/util/Objects", "equals",
                "(Ljava/lang/Object;Ljava/lang/Object;)Z", false));
            approved.add(new VarInsnNode(Opcodes.ALOAD, lifecycle));
            approved.add(new MethodInsnNode(Opcodes.INVOKESTATIC, "com/mojang/serialization/Lifecycle",
                "experimental", "()Lcom/mojang/serialization/Lifecycle;", false));
            approved.add(new MethodInsnNode(Opcodes.INVOKESTATIC, "java/util/Objects", "equals",
                "(Ljava/lang/Object;Ljava/lang/Object;)Z", false));
            approved.add(new InsnNode(Opcodes.IOR));
            approved.add(new JumpInsnNode(Opcodes.IFEQ, branch.label));
            method.instructions.insertBefore(branch, approved);
            method.instructions.remove(branch);
            changed++;
        }
        if (changed != 1) throw new IllegalArgumentException("Expected one reviewed lifecycle branch in " + method.name + method.desc);
    }
    public static void main(String[] paths) throws Exception {
        for (String path : paths) {
            int changed = 0;
            try (ZipFile archive = new ZipFile(path)) {
                Enumeration<? extends ZipEntry> entries = archive.entries();
                while (entries.hasMoreElements()) {
                    ZipEntry entry = entries.nextElement();
                    if (!entry.getName().endsWith(".class")) continue;
                    byte[] bytes;
                    try (java.io.InputStream stream = archive.getInputStream(entry)) {
                        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
                        byte[] block = new byte[16384]; int size;
                        while ((size = stream.read(block)) != -1) out.write(block, 0, size);
                        bytes = out.toByteArray();
                    }
                    byte[] result = patch(bytes);
                    if (result != null) {
                        changed++;
                        System.out.println(path + " " + entry.getName() + " PASS");
                    }
                }
            }
            if (changed == 0) throw new IllegalStateException("No supported experimental prompt in " + path);
        }
    }
}
