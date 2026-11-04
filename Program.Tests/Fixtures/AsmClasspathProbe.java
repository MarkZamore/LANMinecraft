import java.io.InputStream;
import java.util.Collections;

/** Runs in a real -javaagent JVM, where Fabric enumerates the agent's resources. */
public final class AsmClasspathProbe {
    public static void main(String[] args) throws Exception {
        int expectedExternal = Integer.parseInt(args[0]);
        ClassLoader system = ClassLoader.getSystemClassLoader();
        var visible = Collections.list(system.getResources("org/objectweb/asm/ClassReader.class"));
        if (visible.size() != expectedExternal)
            throw new AssertionError("Public ASM copies visible to a loader: " + visible);
        if (expectedExternal == 1) {
            var external = Class.forName("org.objectweb.asm.ClassReader");
            String origin = external.getProtectionDomain().getCodeSource().getLocation().toString();
            if (!origin.endsWith("asm-9.8.jar")) throw new AssertionError("The agent shadowed loader ASM: " + origin);
        }
        byte[] actual;
        try (InputStream input = system.getResourceAsStream("AsmClasspathProbe.class")) {
            if (input == null) throw new AssertionError("No actual fixture class bytes");
            actual = input.readAllBytes();
        }
        for (String prefix : new String[] {"minecraft.portable.internal.asm", "minecraft.portable.notice.internal.asm"}) {
            Class<?> readerType = Class.forName(prefix + ".ClassReader");
            Class<?> visitorType = Class.forName(prefix + ".ClassVisitor");
            Class<?> nodeType = Class.forName(prefix + ".tree.ClassNode");
            Object reader = readerType.getConstructor(byte[].class).newInstance((Object) actual);
            Object node = nodeType.getConstructor(int.class).newInstance(9 << 16);
            readerType.getMethod("accept", visitorType, int.class).invoke(reader, node, 0);
            if (!"AsmClasspathProbe".equals(nodeType.getField("name").get(node)))
                throw new AssertionError("Relocated ASM core/tree did not parse a real class: " + prefix);
        }
        if (Class.forName("minecraft.portable.identity.PortableSkinProfiles", false, null).getClassLoader() != null)
            throw new AssertionError("Game-facing hooks were not isolated on bootstrap");
        if (Class.forName("minecraft.portable.notice.PortableNoticeHooks", false, null).getClassLoader() != null)
            throw new AssertionError("Notice hooks were not isolated on bootstrap");
        if (!Collections.list(system.getResources("RelocateAsm.class")).isEmpty())
            throw new AssertionError("Build-only relocator leaked into runtime agent");
        System.out.println("ASM isolation passed: external=" + expectedExternal + ", private core/tree usable, hooks bootstrap-only.");
    }
}
