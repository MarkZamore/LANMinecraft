import java.lang.reflect.*;
import java.nio.file.*;
import java.util.*;
import java.util.zip.*;

/** Executes the packaged native preflight, independently of the unavailable client browser. */
public final class SteamHostDescriptionPreflightProbe {
    private static final String PREFIX = "minecraft.portable.identity.";
    private static void check(boolean value, String message) { if (!value) throw new AssertionError(message); }
    public static void main(String[] args) throws Exception {
        String minecraft = SteamDiscoveryBehaviorProbe.Minecraft.class.getName().replace('.', '/');
        String user = SteamDiscoveryBehaviorProbe.User.class.getName().replace('.', '/');
        String world = SteamDiscoveryBehaviorProbe.WorldData.class.getName().replace('.', '/');
        Map.of("steamDiscoveryEnabled", "true", "steamDiscoveryClientEnabled", "false",
            "minecraftClasses", minecraft, "steamDiscoveryUserClasses", user,
            "steamDiscoveryWorldDataClasses", world, "steamDiscoveryGetUserMethods", "getUser",
            "steamDiscoveryUserNameMethods", "getName", "steamDiscoveryLevelNameMethods", "getLevelName",
            "lanShareScreenClasses", "fixture/ShareScreen")
            .forEach((key, value) -> System.setProperty(PREFIX + key, value));
        Path zip = Files.createTempFile("steam-host-native-preflight-", ".jar");
        try {
            try (ZipOutputStream output = new ZipOutputStream(Files.newOutputStream(zip))) {
                for (String name : new String[] { minecraft, user, world }) {
                    output.putNextEntry(new ZipEntry(name + ".class"));
                    try (var input = SteamHostDescriptionPreflightProbe.class.getResourceAsStream("/" + name + ".class")) {
                        output.write(input.readAllBytes());
                    }
                    output.closeEntry();
                }
            }
            Class<?> preflight = Class.forName("minecraft.portable.identity.PortableIdentityPreflight");
            Method host = preflight.getDeclaredMethod("verifySteamHostDescription", ZipFile.class, int.class);
            Method publisher = preflight.getDeclaredMethod("verifyLanSharePublishTargets", ZipFile.class, String.class, byte[].class);
            host.setAccessible(true); publisher.setAccessible(true);
            try (ZipFile archive = new ZipFile(zip.toFile())) {
                host.invoke(null, archive, 0);
                for (String property : new String[] { "steamDiscoveryGetUserMethods", "steamDiscoveryUserNameMethods", "steamDiscoveryLevelNameMethods" }) {
                    String previous = System.getProperty(PREFIX + property);
                    System.setProperty(PREFIX + property, "missingHostGetter");
                    try {
                        // The publisher must reject the missing metadata getter before
                        // considering its other native targets, despite client=false.
                        publisher.invoke(null, archive, "fixture/ShareScreen", new byte[0]);
                        throw new AssertionError("Unverified host getter was accepted: " + property);
                    } catch (InvocationTargetException expected) {
                        Throwable cause = expected.getCause();
                        check(cause instanceof IllegalStateException && cause.getMessage().contains(property),
                            "Publisher did not independently validate " + property + ": " + cause);
                    } finally { System.setProperty(PREFIX + property, previous); }
                }
                host.invoke(null, archive, 0);
            }
        } finally { Files.deleteIfExists(zip); }
        System.out.println("PASS: host publication preflight independently rejects each missing metadata getter with local client hooks disabled.");
    }
}
