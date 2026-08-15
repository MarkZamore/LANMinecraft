package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.MethodNode;

public final class PortableSolarFluxSyncTransformer implements ClassFileTransformer {
    private static final String DEFAULT_TARGET =
        "org/zeith/solarflux/client/SolarFluxResourcePack";
    private static final String DEFAULT_METHODS =
        "init,listResources,getNamespaces,getResource";

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        if (!Boolean.getBoolean("minecraft.portable.identity.solarFluxSyncEnabled") ||
            !contains(property("solarFluxPackClasses", DEFAULT_TARGET), className)) {
            return null;
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);

        // The mod's virtual resource pack populates a plain HashMap from init()
        // while the parallel resource reload iterates it from other threads,
        // which intermittently kills the first reload with a
        // ConcurrentModificationException. NeoForge re-runs mod setup on the
        // retried reload and every duplicate-registration guard in the pack
        // then crashes the game. Serializing the pack's methods removes the race.
        int patched = 0;
        String methods = property("solarFluxSyncMethods", DEFAULT_METHODS);
        for (MethodNode method : node.methods) {
            if (!contains(methods, method.name) ||
                (method.access & Opcodes.ACC_STATIC) != 0 ||
                (method.access & Opcodes.ACC_SYNCHRONIZED) != 0) {
                continue;
            }
            method.access |= Opcodes.ACC_SYNCHRONIZED;
            patched++;
        }

        if (patched != 4) {
            System.err.println(
                "[PortableIdentity] SolarFlux resource pack patch failed for " + className +
                ": synchronized " + patched + " methods instead of 4.");
            throw new IllegalStateException(
                "Unsupported SolarFlux resource pack bytecode: expected 4 methods to synchronize, " +
                "found " + patched + ".");
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched SolarFlux resource pack class " + className + ".");
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
}
