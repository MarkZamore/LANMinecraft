package minecraft.portable.notice;

import java.io.InputStream;
import java.lang.instrument.Instrumentation;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.LinkOption;
import java.nio.file.StandardCopyOption;
import java.util.jar.JarFile;

/** Launcher component, loaded by the JVM before a game or loader starts. */
public final class PortableNoticeAgent {
    private static JarFile bootstrap;
    private PortableNoticeAgent() { }

    public static void premain(String arguments, Instrumentation instrumentation) {
        if (System.getProperty("lanminecraft.notice.directory", "").length() == 0) return;
        try {
            Path directory = Paths.get(System.getProperty("lanminecraft.notice.directory")).toAbsolutePath().normalize();
            if (!Files.isDirectory(directory, LinkOption.NOFOLLOW_LINKS))
                throw new IllegalArgumentException("Notification session is not an ordinary directory");
            Path hooks = Files.createTempFile(directory, "lanminecraft-notice-hooks-", ".jar");
            hooks.toFile().deleteOnExit();
            try (InputStream input = PortableNoticeAgent.class.getResourceAsStream("/portable-notice-hooks.jar")) {
                if (input == null) throw new IllegalStateException("Missing bundled notification hooks");
                Files.copy(input, hooks, StandardCopyOption.REPLACE_EXISTING);
            }
            bootstrap = new JarFile(hooks.toFile());
            instrumentation.appendToBootstrapClassLoaderSearch(bootstrap);
            PortableNoticeHooks.start();
            instrumentation.addTransformer(new PortableNoticeTransformer(), false);
            System.out.println("[LauncherNotice] Fullscreen renderer armed for LWJGL 2 and 3.");
        } catch (Throwable failure) {
            System.err.println("[LauncherNotice] Agent initialization failed: " + failure);
        }
    }
}
