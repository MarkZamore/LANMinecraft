package minecraft.portable.notice;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;

/** Instruments the presentation API, independent of Minecraft names and loaders. */
public final class PortableNoticeTransformer implements ClassFileTransformer {
    @Override
    public byte[] transform(ClassLoader loader, String name, Class<?> previous,
                            ProtectionDomain domain, byte[] bytes) {
        if (!"org/lwjgl/glfw/GLFW".equals(name) && !"org/lwjgl/opengl/Display".equals(name)) return null;
        final boolean modern = "org/lwjgl/glfw/GLFW".equals(name);
        final int[] changes = { 0 };
        try {
            ClassReader reader = new ClassReader(bytes);
            ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
            reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
                @Override
                public MethodVisitor visitMethod(int access, String method, String descriptor,
                                                 String signature, String[] exceptions) {
                    MethodVisitor target = super.visitMethod(access, method, descriptor, signature, exceptions);
                    boolean matches = modern
                        ? "glfwSwapBuffers".equals(method) && "(J)V".equals(descriptor)
                        : "swapBuffers".equals(method) && "()V".equals(descriptor);
                    if (!matches || (access & Opcodes.ACC_STATIC) == 0
                        || (access & (Opcodes.ACC_NATIVE | Opcodes.ACC_ABSTRACT)) != 0) return target;
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        @Override public void visitCode() {
                            super.visitCode();
                            visitLdcInsn(Type.getObjectType(name));
                            if (modern) visitVarInsn(Opcodes.LLOAD, 0); else visitInsn(Opcodes.LCONST_0);
                            visitMethodInsn(Opcodes.INVOKESTATIC,
                                "minecraft/portable/notice/PortableNoticeHooks", "present",
                                "(Ljava/lang/Class;J)V", false);
                            changes[0]++;
                        }
                    };
                }
            }, 0);
            if (changes[0] != 1) throw new IllegalStateException("Expected one presentation method, found " + changes[0]);
            PortableNoticeHooks.transformed(modern ? "lwjgl3" : "lwjgl2");
            return writer.toByteArray();
        } catch (Throwable failure) {
            PortableNoticeHooks.failed("Presentation hook for " + name, failure);
            return null;
        }
    }
}
