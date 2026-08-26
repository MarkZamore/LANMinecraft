package minecraft.portable.identity;

import java.io.InputStream;
import java.lang.instrument.Instrumentation;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.jar.JarFile;

public final class PortableIdentityAgent {
    private static JarFile bootstrapJar;

    private PortableIdentityAgent() {
    }

    public static void premain(String arguments, Instrumentation instrumentation) {
        if (!Boolean.getBoolean("minecraft.portable.identity.enabled")) {
            return;
        }

        try {
            // Only the hooks go on the bootstrap search path, out of a jar that
            // holds nothing else. The whole agent used to go there, and it
            // carries a copy of ASM: on the bootstrap path that copy shadows
            // the loader's own and, because a bootstrap class has no code
            // source, Fabric - which finds its libraries by asking where their
            // classes came from - died before the game started with "missing
            // loader library ASM". This was invisible while the adapter only
            // ran on NeoForge.
            Path hooks = Files.createTempFile("portable-identity-hooks", ".jar");
            hooks.toFile().deleteOnExit();
            try (InputStream packed = PortableIdentityAgent.class.getResourceAsStream(
                "/portable-identity-hooks.jar")) {
                if (packed == null) {
                    throw new IllegalStateException("The adapter jar carries no hooks jar.");
                }
                Files.copy(packed, hooks, StandardCopyOption.REPLACE_EXISTING);
            }
            bootstrapJar = new JarFile(hooks.toFile());
            instrumentation.appendToBootstrapClassLoaderSearch(bootstrapJar);
        } catch (Exception exception) {
            throw new IllegalStateException("Portable identity hooks could not be exposed to Minecraft.", exception);
        }

        instrumentation.addTransformer(new PortableIdentityTransformer(), false);
        instrumentation.addTransformer(new PortableXaeroWaypointTransformer(), false);
        instrumentation.addTransformer(new PortableFtbTeleportTransformer(), false);
        instrumentation.addTransformer(new PortableSolarFluxSyncTransformer(), false);
        System.out.println("[PortableIdentity] Stable UUID adapter enabled.");
    }
}
