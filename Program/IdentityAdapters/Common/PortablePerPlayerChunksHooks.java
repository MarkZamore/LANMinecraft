package minecraft.portable.identity;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
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
     * What each player is being served at now, keyed by who he is rather than
     * by which object he currently is. The game builds a new ServerPlayer at
     * every respawn and every trip through a portal, and a note kept against
     * the old one would be lost exactly when somebody dies - which would then
     * be read as "no opinion".
     *
     * This has one meaning and has to keep it: the radius the player is really
     * tracked at. Both halves of a re-track work their rectangle out from what
     * this answers, so a number written here that he is not yet holding strands
     * everything lying between the two.
     */
    private static final Map<UUID, Integer> ASKED = new ConcurrentHashMap<>();

    /** What he has asked for since, and has not been given yet. */
    private static final Map<UUID, Integer> PENDING = new ConcurrentHashMap<>();

    /** When each player was last put back on the chunk map, by nanoTime. */
    private static final Map<UUID, Long> RETRACKED = new ConcurrentHashMap<>();

    /** The game's own floor and ceiling for a view distance. */
    private static final int SMALLEST = 2;
    private static final int LARGEST = 32;

    /** How often one player may cost the tick thread a whole ring. */
    private static final long COOLDOWN = 1_000_000_000L;

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

    /**
     * How far the server was last told to serve, in the scale a player names
     * his own distance in. Written where the game stores it and read back
     * against the field, to see what the game did to the number on the way in.
     */
    private static volatile int lastRequested;

    private static Field viewDistanceField;
    private static Method uuidMethod;

    /**
     * The name lists come from system properties that are set before the game
     * starts and never change afterwards, so each is split once. They are read
     * on paths that run thousands of times a tick.
     */
    private static final Map<String, String[]> ALIASES = new ConcurrentHashMap<>();

    private PortablePerPlayerChunksHooks() {
    }

    /** Called where the server is told how far to serve, before it stores it. */
    public static void observeServerDistance(int requested) {
        lastRequested = requested;
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
            PENDING.put(who, Integer.valueOf(asked));
            settle(player, who);
        } catch (Throwable exception) {
            // A number that could not be read is a player served the way the
            // game would have served him.
        }
    }

    /**
     * Acts on what a player has asked for, if it changes anything and if he has
     * not just been served.
     *
     * The first guard is that a number is only worth acting on when it changes
     * what he would be given. A guest whose slider stands above what the host
     * serves is already receiving everything there is, and moving it about
     * changes nothing whatever - which is also every ordinary join, because the
     * client sends its settings the moment it logs in and the server has put
     * him on the chunk map before that arrives.
     *
     * The second is that acting is dear. Putting a player back on the map
     * forgets and re-sends every chunk of his ring; at thirty-two that is some
     * thousands of chunks serialised on the tick thread, and the game asks for
     * no particular spacing between settings packets while a host's own server
     * rate-limits nothing. Without a floor, one guest holding a key down owns
     * the tick.
     *
     * A number arriving inside that floor is not thrown away, it is left
     * standing: the next one to arrive acts on it, and until then the player
     * keeps the radius he is really tracked at, which is the one thing that has
     * to stay true of ASKED.
     */
    private static void settle(Object player, UUID who) {
        Integer pending = PENDING.get(who);
        if (pending == null) {
            return;
        }
        Object chunkMap = chunkMapOf(player);
        int server = chunkMap == null ? -1 : serverRadius(chunkMap);
        if (server <= 0 || effective(server, pending) == effective(server, ASKED.get(who))) {
            // Nothing he would notice, so the note is the whole of it.
            PENDING.remove(who);
            remember(who, pending);
            return;
        }

        long now = System.nanoTime();
        Long last = RETRACKED.get(who);
        if (last != null && now - last.longValue() < COOLDOWN) {
            return;
        }
        RETRACKED.put(who, Long.valueOf(now));
        Integer before = ASKED.get(who);
        PENDING.remove(who);
        retrack(chunkMap, player, who, before, pending.intValue());
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
    private static void retrack(Object chunkMap, Object player, UUID who, Integer before, int asked) {
        Method update = findUpdate(chunkMap);
        if (update == null) {
            remember(who, Integer.valueOf(asked));
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

    /** The chunk map of the world this player is standing in. */
    private static Object chunkMapOf(Object player) {
        try {
            Object level = PortableIdentityReflection.invoke(
                player,
                aliases("serverLevelMethods", "serverLevel", "x"));
            Object source = PortableIdentityReflection.invoke(
                level,
                aliases("getChunkSourceMethods", "getChunkSource", "k"));
            return PortableIdentityReflection.getField(
                source,
                aliases("chunkMapFields", "chunkMap", "a"));
        } catch (Throwable exception) {
            return null;
        }
    }

    /**
     * The method that adds a player to the map or takes him out of it, found by
     * shape rather than by exact types: the launcher knows its name, and the
     * second argument being a boolean is what separates it from everything else
     * that takes a player.
     */
    private static Method findUpdate(Object chunkMap) {
        String[] names = aliases("updatePlayerStatusMethods", "updatePlayerStatus", "a");
        for (Class<?> type = chunkMap.getClass(); type != null; type = type.getSuperclass()) {
            for (Method candidate : type.getDeclaredMethods()) {
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
        return effective(server, askedFor(player));
    }

    /**
     * The two numbers brought into one scale, and the smaller of them.
     *
     * The field is not kept in the scale a player names his distance in. Until
     * 1.19 the game stored that number plus one and took the one back off at
     * every place it used it; from 1.19 it stores it plainly. Comparing a raw
     * ask against the field would therefore have served everybody on the older
     * versions one ring short of what they asked for - a lone player in his own
     * world included, since the integrated server copies his own number into
     * the server every tick.
     */
    private static int effective(int server, Integer asked) {
        return asked == null ? server : Math.min(server, asked.intValue() + bias(server));
    }

    /**
     * What the game adds to a distance on its way into the field, read rather
     * than remembered: the setter is watched, the field is read afterwards, and
     * the difference is the answer. One on 1.17 to 1.18.2, nothing from 1.19,
     * and anything else is treated as nothing - a bias wrong in that direction
     * costs a ring, and wrong in the other hands out chunks the server never
     * loaded.
     */
    private static int bias(int server) {
        int requested = lastRequested;
        return requested > 0 && server - requested == 1 ? 1 : 0;
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
        Method method = uuidMethod;
        if (method == null || !method.getDeclaringClass().isInstance(player)) {
            method = findUuid(player);
            uuidMethod = method;
        }
        Object value = method.invoke(player);
        return value instanceof UUID ? (UUID) value : null;
    }

    /**
     * getUUID, found once and then kept.
     *
     * It is asked on every tracked entity for every player, which in a busy
     * world is a four-figure number of times a tick. Searching for it afresh
     * each time walked four classes of several hundred methods apiece and threw
     * away three exceptions, each with its stack filled in, on the tick thread
     * - all to arrive at an answer that cannot change for a class.
     */
    private static Method findUuid(Object player) throws NoSuchMethodException {
        String[] names = aliases("playerUuidMethods", "getUUID", "cq");
        for (Class<?> type = player.getClass(); type != null; type = type.getSuperclass()) {
            for (Method candidate : type.getDeclaredMethods()) {
                if (candidate.getParameterCount() != 0) {
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
        // A loader may have left it on an interface instead; the public view of
        // the class carries whatever it inherited from one.
        for (String name : names) {
            try {
                return player.getClass().getMethod(name);
            } catch (NoSuchMethodException ignored) {
                // Try the next spelling.
            }
        }
        throw new NoSuchMethodException("getUUID");
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
        String[] known = ALIASES.get(property);
        if (known != null) {
            return known;
        }
        String value = System.getProperty("minecraft.portable.identity." + property);
        String[] parts;
        if (value == null || value.isBlank()) {
            parts = fallbacks;
        } else {
            parts = value.split(",");
            for (int index = 0; index < parts.length; index++) {
                parts[index] = parts[index].trim();
            }
        }
        ALIASES.put(property, parts);
        return parts;
    }
}
