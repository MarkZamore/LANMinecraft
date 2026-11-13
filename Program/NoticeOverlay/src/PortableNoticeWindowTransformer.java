package minecraft.portable.notice;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.InsnList;
import org.objectweb.asm.tree.LdcInsnNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;
import org.objectweb.asm.tree.VarInsnNode;

/** Installs the name and icons at the native creation boundary, before ShowWindow. */
public final class PortableNoticeWindowTransformer implements ClassFileTransformer {
    private static final String HOOK = "minecraft/portable/notice/PortableNoticeWindowHooks";
    // Read the launch setting before Forge can install its Java 8 security manager.
    private final boolean enabled = !System.getProperty("lanminecraft.window.title", "").isEmpty();

    @Override public byte[] transform(ClassLoader loader, String name, Class<?> previous,
                                      ProtectionDomain domain, byte[] bytes) {
        boolean modern = "org/lwjgl/glfw/GLFW".equals(name);
        boolean windows = "org/lwjgl/opengl/WindowsDisplay".equals(name);
        boolean display = "org/lwjgl/opengl/Display".equals(name);
        if ((!modern && !windows && !display) || !enabled) return null;
        try {
            ClassReader reader = new ClassReader(bytes);
            ClassNode node = new ClassNode(Opcodes.ASM9);
            reader.accept(node, 0);
            int changed = 0;
            for (MethodNode method : node.methods) {
                if ((method.access & (Opcodes.ACC_NATIVE | Opcodes.ACC_ABSTRACT)) != 0) continue;
                if (modern && (method.access & Opcodes.ACC_STATIC) != 0) {
                    if (method.name.equals("nglfwCreateWindow") && method.desc.equals("(IIJJJ)J")) {
                        method.instructions.insert(pointer(name, "beforeCreate"));
                        for (AbstractInsnNode instruction : method.instructions.toArray()) {
                            if (instruction.getOpcode() != Opcodes.LRETURN) continue;
                            InsnList finish = new InsnList();
                            finish.add(new LdcInsnNode(Type.getObjectType(name)));
                            finish.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HOOK, "created", "(JLjava/lang/Class;)J", false));
                            method.instructions.insertBefore(instruction, finish);
                        }
                        changed++;
                    } else if (method.name.equals("nglfwSetWindowTitle") && method.desc.equals("(JJ)V")) {
                        method.instructions.insert(pointer(name, "titlePointer")); changed++;
                    } else if (method.name.equals("glfwWindowHint") && method.desc.equals("(II)V")) {
                        InsnList entry = new InsnList();
                        entry.add(new LdcInsnNode(Type.getObjectType(name)));
                        entry.add(new VarInsnNode(Opcodes.ILOAD, 0));
                        entry.add(new VarInsnNode(Opcodes.ILOAD, 1));
                        entry.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HOOK, "hint", "(Ljava/lang/Class;II)V", false));
                        method.instructions.insert(entry); changed++;
                    } else if (method.name.equals("glfwDefaultWindowHints") && method.desc.equals("()V")) {
                        InsnList entry = new InsnList();
                        entry.add(new LdcInsnNode(Type.getObjectType(name)));
                        entry.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HOOK, "defaultHints", "(Ljava/lang/Class;)V", false));
                        method.instructions.insert(entry); changed++;
                    } else if (method.name.equals("glfwSetWindowIcon") && method.desc.equals("(JLorg/lwjgl/glfw/GLFWImage$Buffer;)V")) {
                        InsnList entry = new InsnList();
                        entry.add(new LdcInsnNode(Type.getObjectType(name)));
                        entry.add(new VarInsnNode(Opcodes.ALOAD, 2));
                        entry.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HOOK, "iconBuffer", "(Ljava/lang/Class;Ljava/lang/Object;)Ljava/lang/Object;", false));
                        entry.add(new org.objectweb.asm.tree.TypeInsnNode(Opcodes.CHECKCAST, "org/lwjgl/glfw/GLFWImage$Buffer"));
                        entry.add(new VarInsnNode(Opcodes.ASTORE, 2));
                        method.instructions.insert(entry); changed++;
                    }
                }
                if ((display || windows) && method.name.equals("setTitle") && method.desc.equals("(Ljava/lang/String;)V")) {
                    int argument = (method.access & Opcodes.ACC_STATIC) != 0 ? 0 : 1;
                    InsnList entry = new InsnList();
                    entry.add(new VarInsnNode(Opcodes.ALOAD, argument));
                    entry.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HOOK, "title", "(Ljava/lang/String;)Ljava/lang/String;", false));
                    entry.add(new VarInsnNode(Opcodes.ASTORE, argument));
                    method.instructions.insert(entry); changed++;
                }
                if ((display || windows) && method.name.equals("setIcon") && method.desc.equals("([Ljava/nio/ByteBuffer;)I")) {
                    int argument = (method.access & Opcodes.ACC_STATIC) != 0 ? 0 : 1;
                    InsnList entry = new InsnList();
                    entry.add(new VarInsnNode(Opcodes.ALOAD, argument));
                    entry.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HOOK, "legacyIcons", "([Ljava/nio/ByteBuffer;)[Ljava/nio/ByteBuffer;", false));
                    entry.add(new VarInsnNode(Opcodes.ASTORE, argument));
                    method.instructions.insert(entry); changed++;
                }
                if (windows && method.name.equals("createWindow") && (method.access & Opcodes.ACC_STATIC) == 0) {
                    for (AbstractInsnNode instruction : method.instructions.toArray()) {
                        if (!(instruction instanceof MethodInsnNode)) continue;
                        MethodInsnNode call = (MethodInsnNode)instruction;
                        if (!call.owner.equals(name) || !call.name.equals("showWindow") || !call.desc.equals("(JI)V")) continue;
                        InsnList entry = new InsnList();
                        entry.add(new VarInsnNode(Opcodes.ALOAD, 0));
                        entry.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HOOK, "beforeLegacyShow", "(Ljava/lang/Object;)V", false));
                        method.instructions.insertBefore(call, entry); changed++;
                    }
                }
            }
            if (changed == 0) return null;
            ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
            node.accept(writer);
            System.out.println("[LauncherWindow] Initial presentation hook installed: " + name + ".");
            return writer.toByteArray();
        } catch (Throwable failure) {
            System.err.println("[LauncherWindow] Window creation hook failed for " + name + ": " + failure);
            return null;
        }
    }

    private static InsnList pointer(String owner, String hook) {
        InsnList entry = new InsnList();
        entry.add(new LdcInsnNode(Type.getObjectType(owner)));
        entry.add(new VarInsnNode(Opcodes.LLOAD, 2));
        entry.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HOOK, hook, "(Ljava/lang/Class;J)J", false));
        entry.add(new VarInsnNode(Opcodes.LSTORE, 2));
        return entry;
    }
}
