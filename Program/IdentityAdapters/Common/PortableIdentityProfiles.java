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
import java.util.concurrent.ConcurrentHashMap;

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
 *
 * <p>A mod can get to that constructor first. e4steam 0.3.0 rewrites the
 * argument of startClientVerification into a profile of its own, keyed by the
 * Steam account that opened the tunnel - one frame after the launcher's login
 * hook handed the server the portable UUID, which is why a guest walked into a
 * friend's world as eedf749f-0e25-39a2-8a84-60146b6343a0 rather than
 * 06c83c9e-980b-47d5-b7be-23d2bb649068 and found none of his things there. That
 * profile is built by this same constructor, so it is caught in this same
 * place: the registry names the UUID the tunnel would give each player, and a
 * profile carrying one is handed back the UUID of the player it belongs to. The
 * name is not consulted for that, because the tunnel chooses that too - the
 * same guest arrived as "Null" one evening and as "MarkZamore" the next.
 */
public final class PortableIdentityProfiles {
    private static final String OfflinePrefix = "OfflinePlayer:";
    // Said once per player, because the evening this was found went on the
    // strength of two log lines that did not obviously belong together.
    private static final Set<UUID> reportedTunnelUuids = ConcurrentHashMap.newKeySet();
    private static volatile long registryModified = Long.MIN_VALUE;
    private static volatile Registry registry = Registry.Empty;

    private PortableIdentityProfiles() {
    }

    /**
     * The UUID this profile should be built with. Everything that is not an
     * offline profile of a player the launcher knows comes back untouched.
     */
    public static UUID remap(UUID id, String name) {
        if (id == null) {
            return id;
        }

        try {
            Registry known = loadRegistry();
            // A profile a Steam tunnel has already stamped with its own idea of
            // who this is. Asked first, and without reading the name, because
            // the tunnel supplies the name as well.
            UUID owned = known.byTunnelUuid.get(id);
            if (owned != null) {
                if (reportedTunnelUuids.add(id)) {
                    System.out.println(
                        "[PortableIdentity] A Steam tunnel offered " + id + "; that player is " + owned + ".");
                }
                return owned;
            }
            if (name == null || name.isEmpty()) {
                return id;
            }
            // Only an offline profile. A profile that already carries a real
            // account's UUID, or one this method has already remapped, does not
            // match its own name's derivation and is left exactly as it is -
            // which is also what makes calling this twice harmless, and it is
            // called twice on authlib 7.0.61 and later, where the two-argument
            // constructor delegates to the three-argument one.
            if (!id.equals(offlineUuid(name))) {
                return id;
            }
            owned = known.byName.get(name);
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

    private static Registry loadRegistry() {
        String configuredPath = System.getProperty("minecraft.portable.identity.registry", "").trim();
        if (configuredPath.isEmpty()) {
            return Registry.Empty;
        }

        try {
            Path path = Path.of(configuredPath);
            long modified = Files.exists(path) ? Files.getLastModifiedTime(path).toMillis() : -1L;
            if (modified == registryModified) {
                return registry;
            }

            Map<String, UUID> byName = new HashMap<>();
            Map<UUID, UUID> byTunnelUuid = new HashMap<>();
            Set<String> ambiguousNames = new HashSet<>();
            Set<UUID> ambiguousTunnelUuids = new HashSet<>();
            if (modified >= 0) {
                for (String line : Files.readAllLines(path, StandardCharsets.UTF_8)) {
                    // The name, the UUID that player's things live under, and -
                    // where the launcher knows their Steam account - the UUID a
                    // Steam tunnel would admit them as. The third field came
                    // later, so a line of two is still a whole line.
                    String[] fields = line.split("\\|", 3);
                    if (fields.length < 2 || fields[0].isEmpty()) {
                        continue;
                    }
                    try {
                        UUID owned = UUID.fromString(fields[1]);
                        UUID previous = byName.put(fields[0], owned);
                        if (previous != null && !previous.equals(owned)) {
                            // Two accounts playing under one name. Guessing
                            // between them would hand one player the other's
                            // things, so neither is remapped and both keep the
                            // UUID the name gives them.
                            ambiguousNames.add(fields[0]);
                        }
                        if (fields.length == 3 && !fields[2].isEmpty()) {
                            UUID tunnelUuid = UUID.fromString(fields[2]);
                            UUID previousOwner = byTunnelUuid.put(tunnelUuid, owned);
                            if (previousOwner != null && !previousOwner.equals(owned)) {
                                ambiguousTunnelUuids.add(tunnelUuid);
                            }
                        }
                    } catch (IllegalArgumentException ignored) {
                        // A half-written line while the launcher rewrites it.
                    }
                }
            }
            byName.keySet().removeAll(ambiguousNames);
            byTunnelUuid.keySet().removeAll(ambiguousTunnelUuids);
            registry = new Registry(
                Collections.unmodifiableMap(byName),
                Collections.unmodifiableMap(byTunnelUuid));
            registryModified = modified;
            return registry;
        } catch (Exception exception) {
            System.err.println("[PortableIdentity] Identity registry could not be read: " + exception.getMessage());
            return registry;
        }
    }

    /**
     * The two ways a profile can name a player the launcher knows: by the name
     * offline Minecraft derives its UUID from, and by the UUID a Steam tunnel
     * stamps on it instead. Kept in one object so a reader never catches half
     * of a reload.
     */
    private static final class Registry {
        static final Registry Empty = new Registry(Collections.emptyMap(), Collections.emptyMap());

        final Map<String, UUID> byName;
        final Map<UUID, UUID> byTunnelUuid;

        Registry(Map<String, UUID> byName, Map<UUID, UUID> byTunnelUuid) {
            this.byName = byName;
            this.byTunnelUuid = byTunnelUuid;
        }
    }
}
