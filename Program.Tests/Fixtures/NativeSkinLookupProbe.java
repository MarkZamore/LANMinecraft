import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
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
        checkPlayerInfoFamily(knownId, hash);
        System.out.println("Texture domain static and instance guard checks passed.");
        System.out.println("Native skin lookup guard checks passed.");
    }

    private static void checkPlayerInfoFamily(UUID knownId, String hash) throws Exception {
        String target = NativePlayerInfo.class.getName().replace('.', '/');
        String skin = "L" + NativePlayerSkin.class.getName().replace('.', '/') + ";";
        System.setProperty("minecraft.portable.identity.playerInfoClasses", target);
        System.setProperty("minecraft.portable.identity.playerInfoSkinEnabled", "true");
        System.setProperty("minecraft.portable.identity.skinLookupMethods", "method_52803");
        System.setProperty("minecraft.portable.identity.skinLookupDescriptors", "(Lcom/mojang/authlib/GameProfile;)Ljava/util/function/Supplier;");
        System.setProperty("minecraft.portable.identity.skinSelectionMethods", "method_52806");
        System.setProperty("minecraft.portable.identity.skinSelectionDescriptors", "(Ljava/util/concurrent/CompletableFuture;" + skin + "Z)" + skin);
        System.setProperty("minecraft.portable.identity.skinSecureMethods", "comp_1630");
        System.setProperty("minecraft.portable.identity.skinTextureUrlMethods", "comp_1911");
        byte[] original;
        try (var input = NativeSkinLookupProbe.class.getResourceAsStream("/" + target + ".class")) { original = input.readAllBytes(); }
        byte[] patched = new PortableIdentityTransformer().transform(null, null, target, null, null, original);
        check(patched != null, "PlayerInfo skin family was disabled with incompatible login hooks.");
        Class<?> type = new Loader().load(patched);
        Method lookup = type.getMethod("method_52803", GameProfile.class);
        GameProfile registered = new GameProfile(knownId, "ActualRemoteFixture");
        GameProfile stranger = new GameProfile(UUID.fromString("00000000-0000-4000-8000-000000000099"), "UnregisteredRemoteFixture");
        check(((Supplier<?>) lookup.invoke(null, registered)).get() == registered, "PlayerInfo native lookup body/profile changed.");
        check(((Supplier<?>) lookup.invoke(null, stranger)).get() == stranger, "Unknown native lookup body changed.");
        check(type.getField("nativeCalls").getInt(null) == 2, "Native PlayerInfo lookup did not run exactly once.");
        check(registered.properties().get("textures") != null && stranger.properties().get("textures") == null,
            "PlayerInfo registration escaped its exact profile.");
        Method select = type.getMethod("method_52806", CompletableFuture.class, NativePlayerSkin.class, boolean.class);
        var fallback = new NativePlayerSkin(null, false);
        String url = "http://127.0.0.1:35658/skin/" + knownId + "/" + hash;
        var portable = new NativePlayerSkin(url, false);
        var forged = new NativePlayerSkin(url + "0", false);
        var foreign = new NativePlayerSkin("https://example.invalid/untrusted", false);
        var official = new NativePlayerSkin("https://textures.minecraft.net/texture/signed", true);
        for (NativePlayerSkin candidate : new NativePlayerSkin[] {portable, forged, foreign, official}) {
            Object actual = select.invoke(null, CompletableFuture.completedFuture(candidate), fallback, true);
            check(actual == ((candidate == portable || candidate == official) ? candidate : fallback),
                "Remote secure selector accepted an unregistered skin or rejected an exact registered/signed skin.");
            check(select.invoke(null, CompletableFuture.completedFuture(candidate), fallback, false) == candidate,
                "Native self insecure lookup semantics changed.");
        }
        check(select.invoke(null, new CompletableFuture<NativePlayerSkin>(), fallback, true) == fallback, "Pending native lookup lost default.");
        check(type.getMethod("unrelatedPacketSentinel").invoke(null).equals(17), "Unrelated PlayerInfo method changed.");
        System.setProperty("minecraft.portable.identity.playerInfoSkinEnabled", "false");
        check(new PortableIdentityTransformer().transform(null, null, target, null, null, original) == null,
            "Explicitly disabled skin family was transformed.");
        System.out.println("Independent PlayerInfo registry-only secure selector checks passed.");
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

    public record NativePlayerSkin(String comp_1911, boolean comp_1630) { }

    public static final class NativePlayerInfo {
        public static int nativeCalls;
        public static Supplier<GameProfile> method_52803(GameProfile profile) {
            nativeCalls++; return () -> profile;
        }
        public static NativePlayerSkin method_52806(CompletableFuture<NativePlayerSkin> future, NativePlayerSkin fallback, boolean secure) {
            NativePlayerSkin candidate = future.getNow(fallback);
            return secure && !candidate.comp_1630() ? fallback : candidate;
        }
        public static int unrelatedPacketSentinel() { return 17; }
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
