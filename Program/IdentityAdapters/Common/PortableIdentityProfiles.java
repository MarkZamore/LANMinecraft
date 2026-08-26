package minecraft.portable.identity;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Collections;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import java.util.UUID;

/**
 * Gives an offline player the UUID their Steam account owns, wherever the game
 * decides to make one.
 *
 * <p>A world keeps a player's inventory in a file named after their UUID, and
 * offline Minecraft derives that UUID from the name alone: version 3 over
 * "OfflinePlayer:" plus the name. So a player who changes machines keeps their
 * things only for as long as they keep their name, and two people who pick the
 * same name are one person to every world they visit.
 *
 * <p>The launcher knows better - it learns each player's Steam account and the
 * UUID derived from it - but only the server that admits them can act on it,
 * and that server is a friend's game on whatever version their pack runs.
 * Patching each version's login code needs that version's mappings, which
 * Fabric does not ship in a form the launcher reads.
 *
 * <p>This is the one place every version agrees on. Whatever builds an offline
 * profile - ServerLoginPacketListenerImpl on the old versions, UUIDUtil on the
 * new ones - it ends at {@code new GameProfile(offlineUuid, name)}, and
 * GameProfile is com.mojang.authlib, which no loader obfuscates. Read out of
 * the real 1.20.1 and 1.21.1 clients, and true by construction anywhere else:
 * the derived UUID is fixed by the protocol, so a profile carrying it is an
 * offline profile no matter who made it.
 */
public final class PortableIdentityProfiles {
    private static final String OfflinePrefix = "OfflinePlayer:";
    private static volatile long registryModified = Long.MIN_VALUE;
    private static volatile Map<String, UUID> entries = Collections.emptyMap();

    private PortableIdentityProfiles() {
    }

    /**
     * The UUID this profile should be built with. Everything that is not an
     * offline profile of a player the launcher knows comes back untouched.
     */
    public static UUID remap(UUID id, String name) {
        if (id == null || name == null || name.isEmpty()) {
            return id;
        }

        try {
            // Only an offline profile. A profile that already carries a real
            // account's UUID, or one this method has already remapped, does not
            // match its own name's derivation and is left exactly as it is -
            // which is also what makes calling this twice harmless, and it is
            // called twice on authlib 7.0.61 and later, where the two-argument
            // constructor delegates to the three-argument one.
            if (!id.equals(offlineUuid(name))) {
                return id;
            }
            UUID owned = loadEntries().get(name);
            return owned == null ? id : owned;
        } catch (RuntimeException failure) {
            // A player who joins under the name's own UUID has the wrong
            // inventory; a player who cannot join at all has none. The first is
            // the better failure.
            System.err.println("[PortableIdentity] Portable UUID lookup failed: " + failure);
            return id;
        }
    }

    /** What offline Minecraft would call this player, on every version. */
    public static UUID offlineUuid(String name) {
        return UUID.nameUUIDFromBytes((OfflinePrefix + name).getBytes(StandardCharsets.UTF_8));
    }

    private static Map<String, UUID> loadEntries() {
        String configuredPath = System.getProperty("minecraft.portable.identity.registry", "").trim();
        if (configuredPath.isEmpty()) {
            return Collections.emptyMap();
        }

        try {
            Path path = Path.of(configuredPath);
            long modified = Files.exists(path) ? Files.getLastModifiedTime(path).toMillis() : -1L;
            if (modified == registryModified) {
                return entries;
            }

            Map<String, UUID> loaded = new HashMap<>();
            Set<String> ambiguous = new HashSet<>();
            if (modified >= 0) {
                for (String line : Files.readAllLines(path, StandardCharsets.UTF_8)) {
                    String[] fields = line.split("\\|", 2);
                    if (fields.length != 2 || fields[0].isEmpty()) {
                        continue;
                    }
                    try {
                        UUID owned = UUID.fromString(fields[1]);
                        UUID previous = loaded.put(fields[0], owned);
                        if (previous != null && !previous.equals(owned)) {
                            // Two accounts playing under one name. Guessing
                            // between them would hand one player the other's
                            // things, so neither is remapped and both keep the
                            // UUID the name gives them.
                            ambiguous.add(fields[0]);
                        }
                    } catch (IllegalArgumentException ignored) {
                        // A half-written line while the launcher rewrites it.
                    }
                }
            }
            for (String name : ambiguous) {
                loaded.remove(name);
            }
            entries = Collections.unmodifiableMap(loaded);
            registryModified = modified;
            return entries;
        } catch (Exception exception) {
            System.err.println("[PortableIdentity] Identity registry could not be read: " + exception.getMessage());
            return entries;
        }
    }
}
