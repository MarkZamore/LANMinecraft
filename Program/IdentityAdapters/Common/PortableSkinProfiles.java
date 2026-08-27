package minecraft.portable.identity;

import java.lang.reflect.Constructor;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Base64;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;

public final class PortableSkinProfiles {
    private static final java.util.concurrent.atomic.AtomicBoolean immutableProfilesReported =
        new java.util.concurrent.atomic.AtomicBoolean();
    private static volatile long registryModified = Long.MIN_VALUE;
    private static volatile Map<UUID, SkinEntry> entries = Collections.emptyMap();

    private PortableSkinProfiles() {
    }

    public static void apply(Object profile) {
        if (profile == null) {
            return;
        }

        try {
            // Read GameProfile's fields, not its getters. getId, getName and
            // getProperties are gone from authlib 7.0.61 (Minecraft 1.21.9),
            // where the class became a record and they are called id, name and
            // properties instead. The three private fields are the one thing
            // that did not move: same names, same types, same order in all 18
            // authlib releases from 1.5.21 to 9.0.75, record or not. So the
            // fields are what this reads, and one code path covers every
            // version the launcher can start.
            UUID id = (UUID) PortableIdentityReflection.getField(profile, "id");
            if (id == null) {
                return;
            }
            SkinEntry entry = loadEntries().get(id);
            if (entry == null) {
                return;
            }

            String name = (String) PortableIdentityReflection.getField(profile, "name");
            String textureJson = createTextureJson(id, name, entry);
            String encoded = Base64.getEncoder().encodeToString(textureJson.getBytes(StandardCharsets.UTF_8));
            ClassLoader loader = profile.getClass().getClassLoader();
            Class<?> propertyType = Class.forName("com.mojang.authlib.properties.Property", true, loader);
            Object property = createProperty(propertyType, encoded);
            Object properties = PortableIdentityReflection.getField(profile, "properties");
            invokeCompatible(properties, "removeAll", "textures");
            invokeCompatible(properties, "put", "textures", property);
        } catch (InvocationTargetException invocation) {
            if (!(invocation.getCause() instanceof UnsupportedOperationException)) {
                throw new IllegalStateException("Portable skin could not be attached to GameProfile.", invocation);
            }
            // From authlib 7.0.61 - Minecraft 1.21.9 - a profile built without
            // properties gets the shared PropertyMap.EMPTY, whose backing map is
            // an ImmutableListMultimap, and GameProfile became a record whose
            // properties field cannot be reassigned either. So the profile
            // cannot be given a skin after it exists; it would have to be built
            // with one, which is a different hook in a different place. Mutating
            // EMPTY is not the way out - every profile without properties shares
            // that one instance, so a skin put there would be everybody's.
            //
            // Said once and then dropped: the pack plays, everyone keeps the
            // UUID the launcher gave them, and only the skin is missing.
            if (immutableProfilesReported.compareAndSet(false, true)) {
                System.out.println(
                    "[PortableIdentity] This Minecraft keeps player profiles unchangeable once made, so the skin "
                        + "chosen in the launcher is not shown. Everything else about the pack is unaffected.");
            }
        } catch (ReflectiveOperationException exception) {
            throw new IllegalStateException("Portable skin could not be attached to GameProfile.", exception);
        }
    }

    /**
     * Whether the skin on this profile is one the launcher put there.
     *
     * <p>Up to authlib 5.0.47 the game asks for a skin and says whether it must
     * be signed, and for another player it always must be. Only Mojang can sign
     * a skin, so a launcher skin never is, and authlib throws the whole reply
     * away with "Signature is missing from textures payload". That is why a
     * player on Minecraft 1.18 saw their own skin and nobody else's: the game
     * asks a second time without the demand for its own player, and never does
     * for anyone else.
     *
     * <p>So the demand is lowered for exactly the profiles this registry knows,
     * and only where {@link #apply} has just taken every other textures
     * property off them. What authlib reads then is the launcher's own line and
     * nothing besides.
     */
    public static boolean isPortableSkin(Object profile) {
        if (profile == null) {
            return false;
        }

        try {
            UUID id = (UUID) PortableIdentityReflection.getField(profile, "id");
            return id != null && loadEntries().containsKey(id);
        } catch (ReflectiveOperationException exception) {
            return false;
        }
    }

    public static Object selectSkin(Object future, Object defaultSkin, boolean requireSecure) {
        if (!(future instanceof CompletableFuture)) {
            return defaultSkin;
        }

        Object candidate = ((CompletableFuture<?>) future).getNow(null);
        if (candidate == null) {
            return defaultSkin;
        }
        if (!requireSecure) {
            return candidate;
        }

        try {
            if (Boolean.TRUE.equals(PortableIdentityReflection.invoke(
                candidate,
                aliases("skinSecureMethods", "secure", "f")))) {
                return candidate;
            }
            Object textureUrl = PortableIdentityReflection.invoke(
                candidate,
                aliases("skinTextureUrlMethods", "textureUrl", "b"));
            if (textureUrl instanceof String && isRegisteredUrl((String) textureUrl)) {
                return candidate;
            }
        } catch (ReflectiveOperationException exception) {
            System.err.println("[PortableIdentity] Portable skin validation failed: " + exception.getMessage());
        }
        return defaultSkin;
    }

    public static boolean isRegisteredUrl(String url) {
        if (!url.startsWith("http://127.0.0.1:")) {
            return false;
        }
        for (SkinEntry entry : loadEntries().values()) {
            if (entry.url().equals(url)) {
                return true;
            }
        }
        return false;
    }

    private static String[] aliases(String propertyName, String... defaults) {
        String value = System.getProperty("minecraft.portable.identity." + propertyName);
        if (value == null || value.isBlank()) {
            return defaults;
        }
        return java.util.Arrays.stream(value.split(","))
            .map(String::trim)
            .filter(candidate -> !candidate.isEmpty())
            .toArray(String[]::new);
    }

    private static Map<UUID, SkinEntry> loadEntries() {
        String configuredPath = System.getProperty("minecraft.portable.skin.registry", "").trim();
        if (configuredPath.isEmpty()) {
            return Collections.emptyMap();
        }

        try {
            Path path = Path.of(configuredPath);
            long modified = Files.exists(path) ? Files.getLastModifiedTime(path).toMillis() : -1L;
            if (modified == registryModified) {
                return entries;
            }

            Map<UUID, SkinEntry> loaded = new HashMap<>();
            if (modified >= 0) {
                for (String line : Files.readAllLines(path, StandardCharsets.UTF_8)) {
                    String[] fields = line.split("\\|", 4);
                    if (fields.length != 4) {
                        continue;
                    }
                    try {
                        UUID id = UUID.fromString(fields[0]);
                        String model = "slim".equalsIgnoreCase(fields[2]) ? "slim" : "classic";
                        if (fields[1].matches("[0-9A-Fa-f]{64}") && fields[3].startsWith("http://127.0.0.1:")) {
                            loaded.put(id, new SkinEntry(fields[3], model));
                        }
                    } catch (IllegalArgumentException ignored) {
                        // Ignore an incomplete line while the launcher refreshes the registry.
                    }
                }
            }
            entries = Collections.unmodifiableMap(loaded);
            registryModified = modified;
            return entries;
        } catch (Exception exception) {
            System.err.println("[PortableIdentity] Skin registry could not be read: " + exception.getMessage());
            return entries;
        }
    }

    private static Object createProperty(Class<?> propertyType, String encoded)
        throws ReflectiveOperationException {
        for (Constructor<?> constructor : propertyType.getConstructors()) {
            Class<?>[] parameters = constructor.getParameterTypes();
            if (parameters.length == 2 && parameters[0] == String.class && parameters[1] == String.class) {
                return constructor.newInstance("textures", encoded);
            }
            if (parameters.length == 3 && parameters[0] == String.class && parameters[1] == String.class &&
                parameters[2] == String.class) {
                return constructor.newInstance("textures", encoded, null);
            }
        }
        throw new NoSuchMethodException(propertyType.getName() + " texture constructor");
    }

    private static Object invokeCompatible(Object target, String name, Object... arguments)
        throws ReflectiveOperationException {
        for (Method method : target.getClass().getMethods()) {
            if (!method.getName().equals(name) || method.getParameterCount() != arguments.length) {
                continue;
            }
            Class<?>[] parameterTypes = method.getParameterTypes();
            boolean compatible = true;
            for (int index = 0; index < arguments.length; index++) {
                if (arguments[index] != null && !parameterTypes[index].isAssignableFrom(arguments[index].getClass())) {
                    compatible = false;
                    break;
                }
            }
            if (compatible) {
                method.setAccessible(true);
                return method.invoke(target, arguments);
            }
        }
        throw new NoSuchMethodException(target.getClass().getName() + "." + name);
    }

    private static String createTextureJson(UUID id, String name, SkinEntry entry) {
        String metadata = "slim".equals(entry.model()) ? ",\"metadata\":{\"model\":\"slim\"}" : "";
        return "{\"timestamp\":" + System.currentTimeMillis() +
            ",\"profileId\":\"" + id.toString().replace("-", "") +
            "\",\"profileName\":\"" + escapeJson(name == null ? "" : name) +
            "\",\"textures\":{\"SKIN\":{\"url\":\"" + escapeJson(entry.url()) + "\"" + metadata + "}}}";
    }

    private static String escapeJson(String value) {
        StringBuilder output = new StringBuilder(value.length() + 16);
        for (int index = 0; index < value.length(); index++) {
            char character = value.charAt(index);
            switch (character) {
                case '\\': output.append("\\\\"); break;
                case '"': output.append("\\\""); break;
                case '\b': output.append("\\b"); break;
                case '\f': output.append("\\f"); break;
                case '\n': output.append("\\n"); break;
                case '\r': output.append("\\r"); break;
                case '\t': output.append("\\t"); break;
                default:
                if (character < 0x20) {
                    output.append(String.format("\\u%04x", (int) character));
                } else {
                    output.append(character);
                }
                    break;
            }
        }
        return output.toString();
    }

    /**
     * A class rather than a record, and the plainer forms above rather than
     * pattern matching, because this jar is built for the oldest Java any pack
     * is started on rather than for the newest.
     */
    private static final class SkinEntry {
        private final String url;
        private final String model;

        SkinEntry(String url, String model) {
            this.url = url;
            this.model = model;
        }

        String url() {
            return url;
        }

        String model() {
            return model;
        }
    }
}
