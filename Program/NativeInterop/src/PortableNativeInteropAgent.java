package minecraft.portable.nativeinterop;

import java.io.InputStream;
import java.lang.instrument.Instrumentation;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.security.MessageDigest;
import java.util.Locale;

/** Launcher runtime interoperability, independent of game, loader and content APIs. */
public final class PortableNativeInteropAgent {
    private PortableNativeInteropAgent() { }

    public static void premain(String arguments, Instrumentation instrumentation) {
        String filename = System.getProperty("lanminecraft.native.openal", "");
        if (filename.length() == 0) return;
        try {
            if (!System.getProperty("os.name", "").toLowerCase(Locale.ROOT).startsWith("windows"))
                return;
            String expected = System.getProperty("lanminecraft.native.openal.sha256", "");
            if (!expected.matches("[0-9a-fA-F]{64}"))
                throw new IllegalArgumentException("Missing or invalid OpenAL SHA256");
            Path library = Paths.get(filename);
            if (!library.isAbsolute()) throw new IllegalArgumentException("OpenAL path must be absolute");
            library = library.normalize();
            String basename = library.getFileName().toString();
            if (!basename.equalsIgnoreCase("OpenAL-amd64.dll") && !basename.equalsIgnoreCase("OpenAL32.dll"))
                throw new IllegalArgumentException("Unexpected OpenAL DLL name");
            for (Path current = library; current != null; current = current.getParent()) {
                if (Files.isSymbolicLink(current))
                    throw new IllegalArgumentException("OpenAL path contains a symbolic link");
            }
            if (!Files.isRegularFile(library, LinkOption.NOFOLLOW_LINKS))
                throw new IllegalArgumentException("OpenAL DLL is not an ordinary file");
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            try (InputStream input = Files.newInputStream(library)) {
                byte[] buffer = new byte[16384];
                for (int read; (read = input.read(buffer)) != -1;) digest.update(buffer, 0, read);
            }
            byte[] expectedBytes = new byte[32];
            for (int i = 0; i < expectedBytes.length; i++)
                expectedBytes[i] = (byte) Integer.parseInt(expected.substring(i * 2, i * 2 + 2), 16);
            if (!MessageDigest.isEqual(expectedBytes, digest.digest()))
                throw new SecurityException("OpenAL DLL SHA256 changed before Java startup");

            // HotSpot's wide Windows loader preserves this Unicode path. The old
            // LWJGL 2 ANSI loader can then find the already loaded module by its
            // ASCII basename, without changing file.encoding or its native DLL.
            System.load(library.toString());
            System.out.println("[LauncherNative] OpenAL preload succeeded");
        } catch (Throwable failure) {
            // Sound initialization remains the game's responsibility. A rejected
            // workaround must not turn a recoverable audio error into a JVM abort.
            System.err.println("[LauncherNative] OpenAL preload failed: " + failure);
        }
    }
}
