package minecraft.portable.identity;

import java.lang.reflect.Field;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;

/**
 * What each player asked to see, on a Minecraft that does not remember.
 *
 * From 1.20.2 the server keeps ServerPlayer.requestedViewDistance and gives
 * every player the smaller of his number and the server's, so raising the
 * server's is the whole feature. Before that there is one number for everybody:
 * raising it to the furthest-asking guest raises it for the guest who set his
 * slider to four and meant it, and he then receives - and stores, and pays the
 * bandwidth for - eight times the chunks he wanted.
 *
 * So the number is caught as it arrives and written down here, and the two
 * places that read the server's number while working on one player are made to
 * ask this instead.
 *
 * Everything fails open, and open means "the number the server would have
 * used". A player nothing is known about, a field that cannot be read, a
 * reflective call that throws: all of them end with the game behaving exactly
 * as it would without any of this. The alternative, failing closed, is a guest
 * standing in an empty world.
 */
public final class PortablePerPlayerChunksHooks {
    /**
     * Keyed by who the player is rather than by which object he currently is.
     * The game builds a new ServerPlayer at every respawn and every trip
     * through a portal, and a note kept against the old one would be lost
     * exactly when somebody dies - which would then be read as "no opinion".
     */
    private static final Map<UUID, Integer> ASKED = new ConcurrentHashMap<>();

    /** The game's own floor and ceiling for a view distance. */
    private static final int SMALLEST = 2;
    private static final int LARGEST = 32;

    /**
     * The last player asked about, and what he wanted.
     *
     * radiusFor is called from inside the loops that decide chunks - some
     * thousands of times for one step across a chunk boundary - and every one
     * of those calls is about the same player as the one before it. Holding the
     * last answer turns all but the first into a comparison of two references.
     * Only ever touched on the server thread, which is where both loops run.
     */
    private static Object lastPlayer;
    private static int lastRadius;

    private static Field viewDistanceField;

    private PortablePerPlayerChunksHooks() {
    }

    /** Called where the server takes a client's settings, before it reads them. */
    public static void observeOptions(Object player, Object packet) {
        try {
            Object value = PortableIdentityReflection.invoke(
                packet,
                aliases("clientViewDistanceMethods", "viewDistance", "c"));
            if (!(value instanceof Integer)) {
                return;
            }
            UUID who = uuidOf(player);
            if (who == null) {
                return;
            }
            int asked = Math.max(SMALLEST, Math.min(((Integer) value).intValue(), LARGEST));
            Integer before = ASKED.get(who);
            if (before != null && before.intValue() == asked) {
                return;
            }
            retrack(player, who, before, asked);
        } catch (Throwable exception) {
            // A number that could not be read is a player served the way the
            // game would have served him.
        }
    }

    /**
     * Tracks this player again, so that a number he has just changed takes
     * effect where he is standing rather than where he next walks.
     *
     * The two methods that decide what a player holds are only called when he
     * moves between chunks or joins, and both work out "what he had" and "what
     * he should have" from the same number - the new one. So a player who
     * raises his slider is not sent the ring he has just asked for: to the
     * game he already had it. Removing him from the map and adding him back is
     * how the game itself handles a player whose view of the world changed
     * wholesale, and it is what a dimension change does.
     *
     * This runs on the server thread. The settings arrive through
     * ServerGamePacketListenerImpl.handleClientInformation, which puts itself
     * on that thread before touching the player - checked in the bytecode of
     * every version this patch is installed on.
     */
    private static void retrack(Object player, UUID who, Integer before, int asked) {
        Object chunkMap = null;
        java.lang.reflect.Method update = null;
        try {
            Object level = PortableIdentityReflection.invoke(
                player,
                aliases("serverLevelMethods", "serverLevel", "x"));
            Object source = PortableIdentityReflection.invoke(
                level,
                aliases("getChunkSourceMethods", "getChunkSource", "k"));
            chunkMap = PortableIdentityReflection.getField(
                source,
                aliases("chunkMapFields", "chunkMap", "a"));
            update = findUpdate(chunkMap);
        } catch (Throwable exception) {
            // Nothing was touched, so nothing has to be put back.
        }
        if (update == null) {
            remember(who, asked);
            return;
        }

        // Taken off the map under the number he is actually holding, and put
        // back under the new one. Both halves work out their rectangle from
        // whatever radiusFor answers at the time, so doing it the other way
        // round - both under the new number - would leave everything between
        // the old radius and the new one on his screen for ever, tracked by
        // nobody and updated by nothing. Where there is no old number he is
        // holding the server's, which is what radiusFor answers with no note.
        try {
            remember(who, before);
            update.invoke(chunkMap, player, Boolean.FALSE);
        } catch (Throwable exception) {
            // Failing to take him off is survivable; failing to put him back is
            // not, so that happens either way, below.
        } finally {
            try {
                remember(who, Integer.valueOf(asked));
                update.invoke(chunkMap, player, Boolean.TRUE);
            } catch (Throwable exception) {
                System.out.println(
                    "[PortableIdentity] a player could not be put back on the chunk map: " + exception);
            }
        }
    }

    /** Writes the note and forgets the one-element cache that repeats it. */
    private static void remember(UUID who, Integer asked) {
        if (asked == null) {
            ASKED.remove(who);
        } else {
            ASKED.put(who, asked);
        }
        lastPlayer = null;
    }

    /**
     * The method that adds a player to the map or takes him out of it, found by
     * shape rather than by exact types: the launcher knows its name, and the
     * second argument being a boolean is what separates it from everything else
     * that takes a player.
     */
    private static java.lang.reflect.Method findUpdate(Object chunkMap) {
        String[] names = aliases("updatePlayerStatusMethods", "updatePlayerStatus", "a");
        for (Class<?> type = chunkMap.getClass(); type != null; type = type.getSuperclass()) {
            for (java.lang.reflect.Method candidate : type.getDeclaredMethods()) {
                Class<?>[] parameters = candidate.getParameterTypes();
                if (parameters.length != 2 || parameters[1] != boolean.class) {
                    continue;
                }
                for (String name : names) {
                    if (candidate.getName().equals(name)) {
                        candidate.setAccessible(true);
                        return candidate;
                    }
                }
            }
        }
        return null;
    }

    /**
     * How far this player should be served, which is the smaller of what the
     * server is serving and what he asked for.
     *
     * This replaces a read of the server's own field, so what it must never do
     * is answer with more than that field held: everything the game does with
     * the number - which chunks to send, which to forget, which tickets to
     * hold - stays inside what the server actually loaded.
     */
    public static int radiusFor(Object chunkMap, Object player) {
        int server = serverRadius(chunkMap);
        if (server <= 0) {
            return server;
        }
        Integer asked = askedFor(player);
        return asked == null ? server : Math.min(server, asked.intValue());
    }

    /**
     * Whether this chunk is one this player asked for.
     *
     * A chunk that has finished loading is offered to everybody the server's
     * radius reaches, and there the player is a loop variable rather than an
     * argument, so the number cannot be narrowed where it is read. It is
     * narrowed here instead, one step later, at the handing over.
     *
     * The comparison is deliberately one ring wider than the number itself. The
     * two methods that decide what a player tracks use the game's own range
     * test, which is generous by about a ring, and this must never be tighter
     * than they are: a chunk they want and this refuses is a hole in his world,
     * while one they do not want and this lets through is a chunk he keeps and
     * nobody misses.
     */
    public static boolean shouldSend(Object chunkMap, Object player, Object chunk) {
        try {
            int radius = radiusFor(chunkMap, player);
            if (radius <= 0) {
                return true;
            }
            Object chunkPos = PortableIdentityReflection.invoke(
                chunk,
                aliases("chunkGetPosMethods", "getPos", "f"));
            Object playerPos = PortableIdentityReflection.invoke(
                player,
                aliases("chunkPositionMethods", "chunkPosition", "dk"));
            int dx = Math.abs(x(chunkPos) - x(playerPos));
            int dz = Math.abs(z(chunkPos) - z(playerPos));
            return Math.max(dx, dz) <= radius + 1;
        } catch (Throwable exception) {
            return true;
        }
    }

    private static int x(Object chunkPos) throws ReflectiveOperationException {
        return intField(chunkPos, "chunkPosXFields", "x", "e");
    }

    private static int z(Object chunkPos) throws ReflectiveOperationException {
        return intField(chunkPos, "chunkPosZFields", "z", "f");
    }

    private static int intField(Object target, String property, String... fallbacks)
        throws ReflectiveOperationException {
        Object value = PortableIdentityReflection.getField(target, aliases(property, fallbacks));
        return ((Integer) value).intValue();
    }

    /**
     * The largest number anybody HERE has asked for, or 0 where nobody present
     * has said anything yet.
     *
     * Asked of the players who are actually on the server rather than of the
     * note, because the note is never cleaned: a guest who visited once and set
     * his slider to thirty-two would otherwise keep the host's world loaded at
     * thirty-two for ever, across worlds and across sessions, and the ceiling
     * would only ever ratchet upwards.
     *
     * Zero is not a distance and the caller must not treat it as one: a world
     * served at nothing, or at the floor of two, is a world that vanished. It
     * means "no opinion", and the right answer to that is to leave the server's
     * own number alone.
     */
    public static int largestAsked(Iterable<?> players) {
        int largest = 0;
        if (players == null) {
            return largest;
        }
        for (Object player : players) {
            Integer asked = null;
            try {
                UUID who = uuidOf(player);
                if (who != null) {
                    asked = ASKED.get(who);
                }
            } catch (Throwable exception) {
                asked = null;
            }
            if (asked != null && asked.intValue() > largest) {
                largest = asked.intValue();
            }
        }
        return largest;
    }

    private static Integer askedFor(Object player) {
        if (player == lastPlayer) {
            return lastRadius == 0 ? null : Integer.valueOf(lastRadius);
        }
        Integer asked = null;
        try {
            UUID who = uuidOf(player);
            if (who != null) {
                asked = ASKED.get(who);
            }
        } catch (Throwable exception) {
            asked = null;
        }
        lastRadius = asked == null ? 0 : asked.intValue();
        lastPlayer = player;
        return asked;
    }

    private static UUID uuidOf(Object player) throws ReflectiveOperationException {
        Object value = PortableIdentityReflection.invoke(
            player,
            aliases("playerUuidMethods", "getUUID", "cq"));
        return value instanceof UUID ? (UUID) value : null;
    }

    /**
     * The number the server is serving, read from the field the patch replaced
     * the reading of. Negative where it cannot be had, which the caller reads
     * as "leave it alone".
     */
    private static int serverRadius(Object chunkMap) {
        try {
            Field field = viewDistanceField;
            if (field == null || !field.getDeclaringClass().isInstance(chunkMap)) {
                field = findViewDistance(chunkMap);
                viewDistanceField = field;
            }
            return field.getInt(chunkMap);
        } catch (Throwable exception) {
            return -1;
        }
    }

    private static Field findViewDistance(Object chunkMap) throws NoSuchFieldException {
        for (String name : aliases("chunkViewDistanceFields", "viewDistance", "O")) {
            for (Class<?> type = chunkMap.getClass(); type != null; type = type.getSuperclass()) {
                try {
                    Field field = type.getDeclaredField(name);
                    field.setAccessible(true);
                    return field;
                } catch (NoSuchFieldException ignored) {
                    // Keep walking: a loader may have moved it up a class.
                }
            }
        }
        throw new NoSuchFieldException("chunk view distance");
    }

    private static String[] aliases(String property, String... fallbacks) {
        String value = System.getProperty("minecraft.portable.identity." + property);
        if (value == null || value.isBlank()) {
            return fallbacks;
        }
        String[] parts = value.split(",");
        for (int index = 0; index < parts.length; index++) {
            parts[index] = parts[index].trim();
        }
        return parts;
    }
}
