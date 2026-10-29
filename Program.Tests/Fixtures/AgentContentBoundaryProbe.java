import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import minecraft.portable.identity.PortableIdentityAgent;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;

/** Runs the real packaged premain against a recording Instrumentation boundary. */
public final class AgentContentBoundaryProbe {
    public static void main(String[] args) throws Exception {
        System.setProperty("minecraft.portable.identity.enabled", "true");
        // Stale process properties must not restore a removed content patch.
        System.setProperty("minecraft.portable.identity.menuRegistrationFixEnabled", "true");
        System.setProperty("minecraft.portable.identity.menuRegistrationFixClasses",
            "net/pedroksl/ae2addonlib/registry/MenuRegistry,com/raishxn/ufo/init/ModMenus");
        List<ClassFileTransformer> registered = new ArrayList<>();
        int[] bootstrap = {0};
        Instrumentation instrumentation = (Instrumentation)Proxy.newProxyInstance(
            AgentContentBoundaryProbe.class.getClassLoader(), new Class<?>[] {Instrumentation.class},
            (proxy, method, arguments) -> {
                if (method.getName().equals("addTransformer")) {
                    registered.add((ClassFileTransformer)arguments[0]); return null;
                }
                if (method.getName().equals("appendToBootstrapClassLoaderSearch")) {
                    bootstrap[0]++; return null;
                }
                throw new AssertionError("Unexpected instrumentation operation: " + method.getName());
            });
        PortableIdentityAgent.premain(null, instrumentation);
        Set<String> names = new HashSet<>();
        for (ClassFileTransformer transformer : registered) names.add(transformer.getClass().getSimpleName());
        Set<String> expected = Set.of("PortableWindowTitleTransformer", "PortableIdentityTransformer",
            "PortableProfileNameTransformer", "PortableXaeroWaypointTransformer", "PortableFtbTeleportTransformer",
            "PortableSolarFluxSyncTransformer", "PortableLanAutoPublishTransformer", "PortableSteamDiscoveryTransformer",
            "PortablePerPlayerChunksTransformer", "PortableGuestMovementTransformer");
        if (!names.equals(expected) || registered.size() != expected.size() || bootstrap[0] != 1)
            throw new AssertionError("Identity/LAN transformer composition changed or content patch remains: " + names);
        int untouched = 0;
        for (String target : List.of("net/pedroksl/ae2addonlib/registry/MenuRegistry", "com/raishxn/ufo/init/ModMenus")) {
            byte[] original = menuClass(target);
            for (ClassFileTransformer transformer : registered) {
                if (transformer.transform(null, null, target, null, null, original) != null)
                    throw new AssertionError("Generic agent still modifies a content menu: " + transformer.getClass().getName());
                untouched++;
            }
        }
        System.out.println("Agent content boundary passed: registrations=" + registered.size() + ", unchangedMenuChecks=" + untouched + ".");
    }
    private static byte[] menuClass(String target) {
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        writer.visit(Opcodes.V17, Opcodes.ACC_PUBLIC, target, null, "java/lang/Object", null);
        var method = writer.visitMethod(Opcodes.ACC_PUBLIC | Opcodes.ACC_STATIC, "make",
            "(Lnet/minecraft/resources/ResourceLocation;)Lnet/minecraft/world/inventory/MenuType;", null, null);
        method.visitCode(); method.visitInsn(Opcodes.ACONST_NULL); method.visitVarInsn(Opcodes.ALOAD, 0);
        method.visitMethodInsn(Opcodes.INVOKEVIRTUAL, "appeng/menu/implementations/MenuTypeBuilder", "build",
            "(Lnet/minecraft/resources/ResourceLocation;)Lnet/minecraft/world/inventory/MenuType;", false);
        method.visitInsn(Opcodes.ARETURN); method.visitMaxs(0, 0); method.visitEnd(); writer.visitEnd();
        return writer.toByteArray();
    }
}
