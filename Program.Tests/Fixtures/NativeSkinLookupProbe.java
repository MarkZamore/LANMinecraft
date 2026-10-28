import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.UUID;
import java.util.function.Supplier;
import com.mojang.authlib.GameProfile;
import minecraft.portable.identity.PortableIdentityTransformer;

/** Executes the injected native lookup guard; this is not Minecraft rendering. */
public final class NativeSkinLookupProbe {
    public static void main(String[] args) throws Exception {
        UUID knownId = UUID.fromString("00000000-0000-4000-8000-000000000001");
        String hash = "1".repeat(64);
        Path registry = Path.of(args[0]);
        Files.writeString(registry, knownId + "|" + hash + "|slim|http://127.0.0.1:35658/skin/" + knownId + "/" + hash,
            StandardCharsets.UTF_8);
        System.setProperty("minecraft.portable.skin.registry", registry.toString());
        System.setProperty("minecraft.portable.identity.identityHooksEnabled", "false");
        System.setProperty("minecraft.portable.identity.modernSkinLookupEnabled", "true");
        String target = NativeLookup.class.getName().replace('.', '/');
        System.setProperty("minecraft.portable.identity.skinManagerClasses", target);
        byte[] original;
        try (var stream = NativeSkinLookupProbe.class.getResourceAsStream("/" + target + ".class")) {
            original = stream.readAllBytes();
        }
        byte[] patched = new PortableIdentityTransformer().transform(null, null, target, null, null, original);
        check(patched != null, "Native lookup was not transformed.");
        Class<?> type = new Loader().load(patched);
        Object manager = type.getConstructor().newInstance();
        Method lookup = type.getMethod("createLookup", GameProfile.class, boolean.class);
        GameProfile known = new GameProfile(knownId, "Known");
        known.properties().put("unrelated", "preserved");
        GameProfile unknown = new GameProfile(UUID.fromString("00000000-0000-4000-8000-000000000099"), "Unknown");
        for (GameProfile profile : new GameProfile[] {known, unknown}) {
            for (boolean requireSecure : new boolean[] {true, false}) {
                Supplier<?> supplier = (Supplier<?>) lookup.invoke(manager, profile, requireSecure);
                check(supplier.get() == profile, "Native supplier or profile was replaced.");
                boolean actual = type.getField("lastSecure").getBoolean(manager);
                check(actual == (profile == known ? false : requireSecure), "Secure guard escaped its registry profile.");
            }
        }
        check(type.getField("nativeCalls").getInt(manager) == 4, "Native lookup did not execute exactly once per call.");
        check("preserved".equals(known.properties().get("unrelated")), "Unrelated property was removed.");
        check(known.properties().get("textures") != null, "Registered profile did not receive its texture.");
        check(unknown.properties().get("textures") == null, "Unknown profile inherited a texture.");
        checkDomainGuard(NativeDiscovery.class, knownId, hash);
        checkDomainGuard(NativeStaticChecker.class, knownId, hash);
        System.out.println("Texture domain static and instance guard checks passed.");
        System.out.println("Native skin lookup guard checks passed.");
    }

    private static void checkDomainGuard(Class<?> originalType, UUID knownId, String hash) throws Exception {
        String target = originalType.getName().replace('.', '/');
        System.setProperty("minecraft.portable.identity.textureUrlCheckerClasses", target);
        byte[] original;
        try (var stream = NativeSkinLookupProbe.class.getResourceAsStream("/" + target + ".class")) {
            original = stream.readAllBytes();
        }
        byte[] patched = new PortableIdentityTransformer().transform(null, null, target, null, null, original);
        check(patched != null, "Texture domain checker was not transformed.");
        Class<?> type = new Loader().load(patched);
        Method method = type.getMethod("isAllowedTextureDomain", String.class);
        Object targetObject = Modifier.isStatic(method.getModifiers()) ? null : type.getConstructor().newInstance();
        String url = "http://127.0.0.1:35658/skin/" + knownId + "/" + hash;
        check(Boolean.TRUE.equals(method.invoke(targetObject, url)), "Registered texture was rejected.");
        check(type.getField("nativeCalls").getInt(targetObject) == 0, "Registered URL missed the exact registry guard.");
        check(Boolean.FALSE.equals(method.invoke(targetObject, url + "0")), "Unregistered loopback texture was accepted.");
        check(Boolean.TRUE.equals(method.invoke(targetObject, "https://textures.minecraft.net/texture/official")),
            "Native official allowlist was bypassed or removed.");
        check(Boolean.FALSE.equals(method.invoke(targetObject, "https://example.invalid/texture/foreign")),
            "Foreign texture was accepted.");
        check(type.getField("nativeCalls").getInt(targetObject) == 3, "Native checker did not execute for nonregistry URLs.");
    }

    public static final class NativeDiscovery {
        public int nativeCalls;

        public boolean isAllowedTextureDomain(String url) {
            nativeCalls++;
            return url.startsWith("https://textures.minecraft.net/texture/");
        }
    }

    public static final class NativeStaticChecker {
        public static int nativeCalls;

        public static boolean isAllowedTextureDomain(String url) {
            nativeCalls++;
            return url.startsWith("https://textures.minecraft.net/texture/");
        }
    }

    public static final class NativeLookup {
        public int nativeCalls;
        public boolean lastSecure;

        public Supplier<GameProfile> createLookup(GameProfile profile, boolean requireSecure) {
            nativeCalls++;
            lastSecure = requireSecure;
            return () -> profile;
        }
    }

    private static final class Loader extends ClassLoader {
        Class<?> load(byte[] bytes) {
            return defineClass(null, bytes, 0, bytes.length);
        }
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
}
