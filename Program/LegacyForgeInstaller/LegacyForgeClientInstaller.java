import java.io.File;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Proxy;
import java.util.Collection;
import java.util.HashSet;

/** Runs the verified legacy installer's client action without opening its Swing UI. */
public final class LegacyForgeClientInstaller {
    private LegacyForgeClientInstaller() { }

    public static void main(String[] args) throws Throwable {
        if (args.length != 1) throw new IllegalArgumentException("Expected the Minecraft installation directory");
        try {
            install(new File(args[0]));
        } catch (InvocationTargetException failure) {
            throw failure.getCause();
        }
    }

    private static void install(File gameRoot) throws ReflectiveOperationException {
        // The old client action uses the same download monitor as the server CLI.
        Class.forName("net.minecraftforge.installer.ServerInstall").getField("headless").setBoolean(null, true);
        Class<?> actions = Class.forName("net.minecraftforge.installer.InstallerAction");
        Object client = actions.getField("CLIENT").get(null);
        if (!Boolean.TRUE.equals(actions.getMethod("isPathValid", File.class).invoke(client, gameRoot))) {
            Object reason = actions.getMethod("getFileError", File.class).invoke(client, gameRoot);
            throw new IllegalStateException(String.valueOf(reason));
        }

        Class<?> versionInfo = Class.forName("net.minecraftforge.installer.VersionInfo");
        Class<?> optionalLibrary = Class.forName("net.minecraftforge.installer.OptionalLibrary");
        Collection<?> optionals = (Collection<?>) versionInfo.getMethod("getOptionals").invoke(null);
        Collection<String> selected = new HashSet<>();
        for (Object optional : optionals) {
            if (Boolean.TRUE.equals(optionalLibrary.getMethod("isClient").invoke(optional)) &&
                    Boolean.TRUE.equals(optionalLibrary.getMethod("getDefault").invoke(optional))) {
                selected.add((String) optionalLibrary.getMethod("getArtifact").invoke(optional));
            }
        }

        Class<?> predicate = Class.forName("com.google.common.base.Predicate");
        Object selection = Proxy.newProxyInstance(predicate.getClassLoader(), new Class<?>[] { predicate },
                (proxy, method, values) -> {
                    switch (method.getName()) {
                        case "apply": return optionals.isEmpty() || selected.contains(values[0]);
                        case "equals": return proxy == values[0];
                        case "hashCode": return System.identityHashCode(proxy);
                        case "toString": return "Legacy Forge client options";
                        default: throw new UnsupportedOperationException(method.getName());
                    }
                });
        if (!Boolean.TRUE.equals(actions.getMethod("run", File.class, predicate).invoke(client, gameRoot, selection))) {
            throw new IllegalStateException("Legacy Forge client installation failed");
        }
    }
}
