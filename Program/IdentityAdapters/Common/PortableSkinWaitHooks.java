package minecraft.portable.identity;

import java.util.Collections;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.WeakHashMap;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

/**
 * Waits, once, for a skin the launcher itself is serving.
 *
 * <p>SkinManager.getInsecureSkin does not wait: it asks the loader what it has
 * this instant and hands back the default face when the answer is "not yet".
 * That is the right answer for a skin coming over the internet and the wrong
 * one for a skin coming from 127.0.0.1, and it is the only door some mods use.
 * Yes Steve Model takes it once, when it builds a player's model, and keeps
 * whatever it got - so a face that was a few milliseconds late is a stranger
 * for the rest of the session.
 *
 * <p>So for a profile the launcher has a skin for, and only the first time it
 * is asked about, this waits a fraction of a second for the real answer. The
 * file is on the same machine; if it is not there in that time something else
 * is wrong and the caller gets exactly what it would have got anyway.
 */
public final class PortableSkinWaitHooks {
    /**
     * Long enough for a local HTTP round trip and short enough not to be seen.
     * The skin is served by the launcher over the loopback interface, where a
     * request is answered in single-digit milliseconds; this is the ceiling for
     * a machine that is busy, not the expected wait.
     */
    private static final long WAIT_MS = 250L;

    /** One wait per profile: a second one would be a stall for no new answer. */
    private static final Set<UUID> WAITED =
        Collections.newSetFromMap(Collections.synchronizedMap(new WeakHashMap<>()));

    private PortableSkinWaitHooks() {
    }

    /**
     * The skin for this profile, or null to let the game answer as it always
     * has. Never throws: every failure here is a face, not a crash.
     */
    public static Object awaitSkin(Object skinManager, Object profile) {
        if (!Boolean.getBoolean("minecraft.portable.identity.skinWaitEnabled") ||
            skinManager == null ||
            profile == null) {
            return null;
        }

        try {
            if (!PortableSkinProfiles.isPortableSkin(profile)) {
                return null;
            }

            Object id = PortableIdentityReflection.getField(profile, "id");
            if (!(id instanceof UUID) || !WAITED.add((UUID) id)) {
                return null;
            }

            Object pending = PortableIdentityReflection.invokeDeclared(
                skinManager,
                new Class<?>[] { profile.getClass() },
                new Object[] { profile },
                aliases("skinOrLoadMethods", "getOrLoad", "c"));
            if (!(pending instanceof CompletableFuture)) {
                return null;
            }
            Object skin = ((CompletableFuture<?>) pending).get(WAIT_MS, TimeUnit.MILLISECONDS);
            if (skin != null) {
                System.out.println("[PortableIdentity] Waited for the portable skin of " + id + ".");
            }
            return skin;
        } catch (Throwable exception) {
            // A timeout is the ordinary case here, not an error: the game then
            // answers with the default face exactly as it did before.
            return null;
        }
    }

    private static String[] aliases(String propertyName, String... defaults) {
        String value = System.getProperty("minecraft.portable.identity." + propertyName);
        if (value == null || value.isBlank()) {
            return defaults;
        }
        Map<String, Boolean> seen = new java.util.LinkedHashMap<>();
        for (String candidate : value.split(",")) {
            String trimmed = candidate.trim();
            if (!trimmed.isEmpty()) {
                seen.put(trimmed, Boolean.TRUE);
            }
        }
        return seen.keySet().toArray(new String[0]);
    }
}
