package minecraft.portable.notice;

import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;
import java.util.Properties;

/** Bytecode contract checks, run against the actual packaged transformer. */
public final class TransformerAudit {
    public static void main(String[] arguments) {
        audit("org/lwjgl/glfw/GLFW", "glfwSwapBuffers", "(J)V", false);
        audit("org/lwjgl/opengl/Display", "swapBuffers", "()V", false);
        audit("org/lwjgl/glfw/GLFW", "glfwSwapBuffers", "(J)V", true);
        byte[] unrelated = fixture("example/Unrelated", "swapBuffers", "()V", false);
        if (new PortableNoticeTransformer().transform(null, "example/Unrelated", null, null, unrelated) != null) {
            throw new AssertionError("Unrelated classes must be untouched");
        }
        snapshotContracts();
        System.out.println("Presentation transformer contracts passed (LWJGL2, LWJGL3, native refusal, unrelated class).");
    }

    private static void snapshotContracts() {
        long now = 1800000000000L;
        Properties data = new Properties();
        data.setProperty("count", "1");
        data.setProperty("0.title", "Друг Оскар");
        data.setProperty("0.body", "Зашёл в сборку");
        data.setProperty("0.createdUnixMs", Long.toString(now - 100));
        PortableNoticeHooks.Notice[] parsed = PortableNoticeHooks.parse(data, now);
        if (parsed.length != 1 || !data.getProperty("0.title").equals(parsed[0].title)) {
            throw new AssertionError("Unicode notification was not preserved");
        }
        if (PortableNoticeHooks.parse(data, now + 7001).length != 0) throw new AssertionError("Notification did not expire");
        data.setProperty("count", "4");
        try {
            PortableNoticeHooks.parse(data, now);
            throw new AssertionError("Unbounded snapshot was accepted");
        } catch (IllegalArgumentException expected) { }
        System.out.println("Snapshot contracts passed (Unicode, expiry, bounds).");
    }

    private static void audit(String name, String method, String signature, boolean nativeMethod) {
        byte[] source = fixture(name, method, signature, nativeMethod);
        byte[] changed = new PortableNoticeTransformer().transform(null, name, null, null, source);
        if (nativeMethod) {
            if (changed != null) throw new AssertionError("Native presentation cannot receive Java bytecode");
            return;
        }
        if (changed == null) throw new AssertionError("Presentation wrapper was not instrumented");
        final int[] hooks = { 0 };
        new ClassReader(changed).accept(new ClassVisitor(Opcodes.ASM9) {
            @Override public MethodVisitor visitMethod(int access, String methodName, String descriptor,
                                                        String signature, String[] exceptions) {
                return new MethodVisitor(Opcodes.ASM9) {
                    @Override public void visitMethodInsn(int opcode, String owner, String called,
                                                          String descriptor, boolean isInterface) {
                        if ("minecraft/portable/notice/PortableNoticeHooks".equals(owner) && "present".equals(called)) {
                            if (opcode != Opcodes.INVOKESTATIC || !"(Ljava/lang/Class;J)V".equals(descriptor)) {
                                throw new AssertionError("Presentation hook signature changed");
                            }
                            hooks[0]++;
                        }
                    }
                };
            }
        }, 0);
        if (hooks[0] != 1) throw new AssertionError("Expected one hook, found " + hooks[0]);
    }

    private static byte[] fixture(String name, String method, String signature, boolean nativeMethod) {
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        writer.visit(Opcodes.V1_8, Opcodes.ACC_PUBLIC, name, null, "java/lang/Object", null);
        MethodVisitor target = writer.visitMethod(Opcodes.ACC_PUBLIC | Opcodes.ACC_STATIC |
            (nativeMethod ? Opcodes.ACC_NATIVE : 0), method, signature, null, null);
        if (!nativeMethod) {
            target.visitCode();
            target.visitInsn(Opcodes.RETURN);
            target.visitMaxs(0, 0);
        }
        target.visitEnd();
        writer.visitEnd();
        return writer.toByteArray();
    }
}
