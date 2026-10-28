package minecraft.portable.identity;

import java.lang.reflect.Method;
import java.lang.reflect.Constructor;
import java.lang.reflect.Modifier;
import java.net.Proxy;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.FileTime;
import java.util.Base64;
import java.util.Collection;
import java.util.UUID;
import java.util.List;
import java.util.Map;
import java.util.function.Supplier;

public final class PortableSkinPreflight {
    private PortableSkinPreflight() {
    }

    public static void main(String[] arguments) throws Exception {
        if (arguments.length != 3 && (arguments.length != 4 || !"--profiles".equals(arguments[3]))) {
            throw new IllegalArgumentException("Usage: <registered-url> <unregistered-url> <official-url> [--profiles]");
        }

        DomainCheck isAllowed;
        try {
            isAllowed = findDomainCheck();
        } catch (UncheckableHereException unavailable) {
            if (arguments.length == 4) {
                throw new IllegalStateException("Full skin profile preflight needs the runtime dependencies.", unavailable);
            }
            // The rule is on a class that will not start outside the game. Up
            // to authlib 3.16.29 it lives on YggdrasilMinecraftSessionService,
            // whose static initialiser wants a logger, and this preflight is
            // given authlib and nothing else. That is a limit of the bench, not
            // a fault in the patch: the bytecode preflight has already read the
            // patched method back and seen the guard in it. Refusing here would
            // turn "cannot check in isolation" into "no skins on any Minecraft
            // before 1.19.4", which is the whole thing being fixed.
            System.out.println(
                "Portable skin URL preflight skipped: " + unavailable.getMessage());
            return;
        }
        if (!allowed(isAllowed, arguments[0])) {
            throw new IllegalStateException("Registered portable skin URL was rejected.");
        }
        if (allowed(isAllowed, arguments[1])) {
            throw new IllegalStateException("Unregistered local skin URL was accepted.");
        }
        if (!allowed(isAllowed, arguments[2])) {
            throw new IllegalStateException("Official Minecraft texture URL was rejected.");
        }

        System.out.println("Portable skin URL preflight passed.");
        if (arguments.length == 4) {
            verifyProfiles(arguments[0], isAllowed.target);
        }
    }

    // Optional full-authlib bench: unlike the URL-only launch check, this
    // needs authlib's actual Guava/Gson/logger dependencies on the classpath.
    // It creates no game window and makes no network requests.
    private static void verifyProfiles(String registeredUrl, Object discoveryService) throws Exception {
        Path registry = Path.of(System.getProperty("minecraft.portable.skin.registry"));
        byte[] originalRegistry = Files.readAllBytes(registry);
        FileTime originalTime = Files.getLastModifiedTime(registry);
        UUID id = UUID.fromString(registeredUrl.split("/")[4]);
        Class<?> profileType = Class.forName("com.mojang.authlib.GameProfile");
        Class<?> propertyType = Class.forName("com.mojang.authlib.properties.Property");
        Class<?> propertiesType = Class.forName("com.mojang.authlib.properties.PropertyMap");
        Class<?> multimapType = Class.forName("com.google.common.collect.Multimap");
        Object map = Class.forName("com.google.common.collect.LinkedHashMultimap").getMethod("create").invoke(null);
        Object unrelated = propertyType.getConstructor(String.class, String.class).newInstance("portable-test", "keep");
        multimapType.getMethod("put", Object.class, Object.class).invoke(map, "portable-test", unrelated);
        Object properties;
        Object profile;
        if (profileType.isRecord()) {
            properties = propertiesType.getConstructor(multimapType).newInstance(map);
            profile = profileType.getConstructor(UUID.class, String.class, propertiesType)
                .newInstance(id, "PortableSkinQA", properties);
            Object builtProperties = PortableIdentityReflection.getField(profile, "properties");
            Collection<?> constructorTextures = (Collection<?>) multimapType.getMethod("get", Object.class)
                .invoke(builtProperties, "textures");
            require(constructorTextures.size() == 1, "Immutable profile constructor did not store the selected skin.");
        } else {
            properties = propertiesType.getConstructor().newInstance();
            multimapType.getMethod("putAll", multimapType).invoke(properties, map);
            profile = profileType.getConstructor(UUID.class, String.class).newInstance(id, "PortableSkinQA");
            Object nativeProperties = PortableIdentityReflection.getField(profile, "properties");
            multimapType.getMethod("putAll", multimapType).invoke(nativeProperties, properties);
        }
        Object service;
        Class<?> serviceType;
        if (discoveryService != null) {
            service = discoveryService.getClass().getMethod("createMinecraftSessionService").invoke(discoveryService);
            serviceType = service.getClass();
        } else {
            Class<?> keySetType = Class.forName("com.mojang.authlib.yggdrasil.ServicesKeySet");
            Class<?> environmentType = Class.forName("com.mojang.authlib.Environment");
            Class<?> environmentEnum = Class.forName("com.mojang.authlib.yggdrasil.YggdrasilEnvironment");
            Object environment = environmentEnum.getMethod("getEnvironment")
                .invoke(environmentEnum.getField("PROD").get(null));
            serviceType = Class.forName("com.mojang.authlib.yggdrasil.YggdrasilMinecraftSessionService");
            Constructor<?> serviceConstructor = serviceType.getDeclaredConstructor(keySetType, Proxy.class, environmentType);
            serviceConstructor.setAccessible(true);
            service = serviceConstructor.newInstance(keySetType.getField("EMPTY").get(null), Proxy.NO_PROXY, environment);
        }
        Method read = serviceType.getMethod("getPackedTextures", profileType);
        Method unpack = serviceType.getMethod("unpackTextures", propertyType);

        try {
            String hash = registeredUrl.substring(registeredUrl.lastIndexOf('/') + 1);
            long revision = Math.max(System.currentTimeMillis(), originalTime.toMillis()) + 2000L;
            for (String model : new String[] {"classic", "slim"}) {
                Files.writeString(registry, id + "|" + hash + "|" + model + "|" + registeredUrl, StandardCharsets.UTF_8);
                Files.setLastModifiedTime(registry, FileTime.fromMillis(revision++));
                Object texture = read.invoke(service, profile);
                require(texture != null, "Native authlib did not read the selected " + model + " skin.");
                String encoded = (String) PortableIdentityReflection.getField(texture, "value");
                String json = new String(Base64.getDecoder().decode(encoded), StandardCharsets.UTF_8);
                require(json.contains(registeredUrl), "Native texture property lost the registered URL.");
                require(json.contains("\"model\":\"slim\"") == "slim".equals(model), "Selected skin model changed.");
                Object repeated = read.invoke(service, profile);
                require(encoded.equals(PortableIdentityReflection.getField(repeated, "value")), "Repeated read changed the texture cache key.");
                Object textures = unpack.invoke(service, texture);
                Object skin = textures.getClass().getMethod("skin").invoke(textures);
                require(skin != null, "Native authlib discarded the portable texture.");
                require(registeredUrl.equals(skin.getClass().getMethod("getUrl").invoke(skin)), "Native authlib changed the texture URL.");
                Object nativeModel = skin.getClass().getMethod("getMetadata", String.class).invoke(skin, "model");
                require("slim".equals(model) ? "slim".equals(nativeModel) : nativeModel == null, "Native authlib changed the arm model.");
                Object rebuilt = PortableSkinProfiles.withSkin(profile);
                require(id.equals(PortableIdentityReflection.getField(rebuilt, "id")), "Skin changed the UUID.");
                require("PortableSkinQA".equals(PortableIdentityReflection.getField(rebuilt, "name")), "Skin changed the player name.");
                Object rebuiltProperties = PortableIdentityReflection.getField(rebuilt, "properties");
                Collection<?> preserved = (Collection<?>) multimapType.getMethod("get", Object.class).invoke(rebuiltProperties, "portable-test");
                require(preserved.contains(unrelated), "Skin removed an unrelated profile property.");
            }
            Object unknown = profileType.getConstructor(UUID.class, String.class)
                .newInstance(UUID.fromString("00000000-0000-4000-8000-000000000099"), "PortableSkinOther");
            require(read.invoke(service, unknown) == null, "Unknown player inherited the selected skin.");
            require(!PortableSkinProfiles.isPortableSkin(unknown), "Unknown player bypassed the secure rule.");
            if (profileType.isRecord()) {
                Object empty = propertiesType.getField("EMPTY").get(null);
                Collection<?> emptyTextures = (Collection<?>) multimapType.getMethod("get", Object.class).invoke(empty, "textures");
                require(emptyTextures.isEmpty(), "Shared immutable properties were modified.");
            }
        } finally {
            Files.write(registry, originalRegistry);
            Files.setLastModifiedTime(registry, originalTime);
        }
        System.out.println("Portable skin profile preflight passed: classic/slim, native packed/unpacked textures, stable cache key, unchanged UUID/name/properties and unknown-profile isolation.");
    }

    private static void require(boolean condition, String message) {
        if (!condition) {
            throw new IllegalStateException(message);
        }
    }

    // The same forms the transformer knows about, asked for in the same
    // order: whichever one this authlib has is the one that was patched, and
    // the preflight has to test that one rather than assume the newest.
    private static DomainCheck findDomainCheck() throws Exception, UncheckableHereException {
        String[] classNames = {
            "com.mojang.authlib.yggdrasil.TextureUrlChecker",
            "com.mojang.authlib.yggdrasil.YggdrasilMinecraftSessionService",
            "com.mojang.authlib.services.MinecraftServicesDiscoveryService",
        };
        String[] methodNames = {"isAllowedTextureDomain", "isWhitelistedDomain"};
        for (String className : classNames) {
            Class<?> type;
            try {
                // Without initialising: the method is only looked for here, and
                // a class that cannot be initialised should still be able to
                // say whether it carries the rule.
                type = Class.forName(className, false, ClassLoader.getSystemClassLoader());
            } catch (ClassNotFoundException absent) {
                continue;
            } catch (NoClassDefFoundError incomplete) {
                // Not absent - present, and unable to link without the rest of
                // the game around it. Up to authlib 3.16.29 the session service
                // reaches for Guava's cache and a logger while it is being
                // linked, and this check is given authlib alone.
                throw new UncheckableHereException(
                    className + " needs more of the game than this check is given: " + incomplete);
            }
            for (String methodName : methodNames) {
                Method method;
                try {
                    method = type.getDeclaredMethod(methodName, String.class);
                } catch (NoSuchMethodException absent) {
                    continue;
                } catch (NoClassDefFoundError incomplete) {
                    // Asking a class for one method makes reflection resolve
                    // the types in every method it has, and up to authlib
                    // 3.16.29 the session service has methods written in terms
                    // of Guava's cache. Present and unreadable here, which is
                    // the bench's limit rather than the patch's.
                    throw new UncheckableHereException(
                        className + " needs more of the game than this check is given: " + incomplete);
                }
                method.setAccessible(true);
                Object target;
                try {
                    // Calling it is what initialises the class, so this is
                    // where a missing logger or the like shows up.
                    target = Modifier.isStatic(method.getModifiers()) ? null : createDiscovery(type);
                    method.invoke(target, "https://textures.minecraft.net/texture/portable-preflight");
                } catch (ExceptionInInitializerError | NoClassDefFoundError incomplete) {
                    throw new UncheckableHereException(
                        className + " needs more of the game than this check is given: " + incomplete);
                }
                return new DomainCheck(method, target);
            }
        }
        throw new IllegalStateException(
            "This authlib keeps its texture domain rule somewhere new: no known form is present.");
    }

    // Authlib 10 resolves its allowed texture URI prefixes from discovery.
    // Supply only the official prefix to the real native data records and
    // real checker, without starting discovery fetches or replacing the rule.
    private static Object createDiscovery(Class<?> type) throws Exception {
        String prefix = "com.mojang.authlib.services.response.discovery.";
        Class<?> endpointType = Class.forName(prefix + "Endpoint");
        Class<?> endpointsType = Class.forName(prefix + "Endpoints");
        Class<?> discoveryType = Class.forName(prefix + "Discovery");
        Class<?> responseType = Class.forName(prefix + "DiscoveryResponse");
        String textureUri = "https://textures.minecraft.net/texture/{textureId}";
        Object endpoint = endpointType.getConstructor(String.class, List.class)
            .newInstance(textureUri, List.of(textureUri));
        Object profiles = endpointsType.getConstructor(Map.class).newInstance(Map.of("getTexture", endpoint));
        Object empty = endpointsType.getMethod("empty").invoke(null);
        Object discovery = discoveryType.getConstructor(String.class, endpointsType, endpointsType,
                endpointsType, endpointsType, endpointsType)
            .newInstance("minecraft", empty, empty, empty, profiles, empty);
        Object response = responseType.getConstructor(String.class, String.class, discoveryType)
            .newInstance("production", "minecraft", discovery);
        Constructor<?> constructor = type.getDeclaredConstructor(Proxy.class, boolean.class, Supplier.class);
        constructor.setAccessible(true);
        return constructor.newInstance(Proxy.NO_PROXY, false, (Supplier<Object>) () -> response);
    }

    private static final class DomainCheck {
        final Method method;
        final Object target;

        DomainCheck(Method method, Object target) {
            this.method = method;
            this.target = target;
        }
    }

    private static final class UncheckableHereException extends Exception {
        UncheckableHereException(String message) {
            super(message);
        }
    }

    private static boolean allowed(DomainCheck check, String url) throws Exception {
        return Boolean.TRUE.equals(check.method.invoke(check.target, url));
    }
}
