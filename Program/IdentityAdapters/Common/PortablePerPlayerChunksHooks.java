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
 *
 * Every name this needs is found once and kept. That is not tidiness: these
 * methods sit inside the loops that decide chunks, and one of them runs for
 * every chunk handed to every player. Searching for a name there - walking a
 * class hierarchy and throwing away a stack trace at each miss - cost a server
 * seventy seconds of tick in a modpack the same machine serves at thirty-two
 * chunks when this patch is not installed.
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

    // Everything the hot paths reach for, each found once. The fallbacks are
    // the 1.20.1 obfuscated spellings, used only if the launcher named nothing.
    private static final Member CHUNK_VIEW_DISTANCE = new Member("chunkViewDistanceFields", "viewDistance", "O");
    private static final Member PLAYER_UUID = new Member("playerUuidMethods", "getUUID", "cq");
    private static final Member CHUNK_POSITION_OF = new Member("chunkGetPosMethods", "getPos", "f");
    private static final Member PLAYER_CHUNK_POSITION = new Member("chunkPositionMethods", "chunkPosition", "dk");
    private static final Member CHUNK_POS_X = new Member("chunkPosXFields", "x", "e");
    private static final Member CHUNK_POS_Z = new Member("chunkPosZFields", "z", "f");
    private static final Member PLAYER_LEVEL = new Member("serverLevelMethods", "serverLevel", "x");
    private static final Member LEVEL_CHUNK_SOURCE = new Member("getChunkSourceMethods", "getChunkSource", "k");
    private static final Member SOURCE_CHUNK_MAP = new Member("chunkMapFields", "chunkMap", "a");
    private static final Member ASKED_DISTANCE = new Member("clientViewDistanceMethods", "viewDistance", "c");

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

    /**
     * The last number the server's own field actually gave up, and whether the
     * failure to read it has been mentioned. Said once: this sits inside the
     * loops that decide chunks, and a line per chunk would bury the log it is
     * meant to explain.
     */
    private static volatile int lastGoodRadius;
    private static volatile boolean complainedAboutRadius;

    private PortablePerPlayerChunksHooks() {
    }

    /**
     * Called where the server is told how far to serve, before it stores it.
     *
     * This one is not allowed to throw. It is the head of a method the game
     * calls from ChunkMap's own constructor, so anything that escapes here
     * takes the world down with it before it has finished loading - and the
     * whole point of the hook is a number that only makes the answer nicer.
     */
    public static void observeServerDistance(int requested) {
        lastRequested = requested;
        try {
            // A host moving his own slider narrows the world for everybody, and
            // until this the launcher only noticed on its next five second
            // look. Nothing is done there beyond saying so: this runs on the
            // tick thread, inside the setter, before the field it will write.
            PortableLanAutoPublishHooks.serverDistanceChanged(requested);
        } catch (Throwable exception) {
            // Then the keeper finds out on its own clock, as it always did.
        }
    }

    /** Called where the server takes a client's settings, before it reads them. */
    public static void observeOptions(Object player, Object packet) {
        try {
            Object value = ASKED_DISTANCE.method(packet).invoke(packet);
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
        Method update = findUpdate(chunkMap, player);
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
            Object level = PLAYER_LEVEL.method(player).invoke(player);
            Object source = LEVEL_CHUNK_SOURCE.method(level).invoke(level);
            return SOURCE_CHUNK_MAP.field(source).get(source);
        } catch (Throwable exception) {
            return null;
        }
    }

    /**
     * The method that adds a player to the map or takes him out of it.
     *
     * Found by shape as well as by name, and the shape is asked about this
     * player rather than about types in the abstract. A chunk map has a second
     * method of exactly the same outline - two arguments, the second a boolean,
     * and on an obfuscated runtime the same one-letter name - which answers
     * which players can see a chunk. Taking that one would throw at the call
     * and leave the player holding a radius he is not tracked at. Asking
     * whether the first argument would accept the player in hand tells them
     * apart without depending on the loader having renamed anything.
     */
    private static Method findUpdate(Object chunkMap, Object player) {
        String[] names = aliases("updatePlayerStatusMethods", "updatePlayerStatus", "a");
        for (Class<?> type = chunkMap.getClass(); type != null; type = type.getSuperclass()) {
            for (Method candidate : type.getDeclaredMethods()) {
                Class<?>[] parameters = candidate.getParameterTypes();
                if (parameters.length != 2 || parameters[1] != boolean.class ||
                    !parameters[0].isInstance(player)) {
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
        // Never a number the game cannot work with. What this answers replaces
        // a field read, and the game does arithmetic on it at once: loop bounds
        // for which chunks a player holds, and (n - 1) * 16 for how far he is
        // told about entities. A negative here is not "leave it alone", which
        // is what this used to believe - it is a player standing in an empty
        // world whose every entity flickers out of tracking and back, jumping
        // as it returns. Where the server's own number cannot be had, the last
        // one that could is the honest answer; before there has ever been one,
        // the game's own floor is.
        if (server <= 0) {
            return lastGoodRadius > 0 ? lastGoodRadius : SMALLEST + 1;
        }
        int answer = effective(server, askedFor(player));
        return answer > 0 ? answer : server;
    }

    /**
     * The two numbers brought into one scale, and the smaller of them.
     *
     * The field is not kept in the scale a player names his distance in. Until
     * 1.20 the game stored that number plus one and took the one back off at
     * every place it used it; from 1.20 it stores it plainly. Comparing a raw
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
     * the difference is the answer. One on 1.17 to 1.19.4, nothing from 1.20,
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
     * This is the hottest thing in the patch: it runs once for every chunk
     * given to every player, and a whole ring of them goes past whenever a
     * distance changes. Everything it touches is found once and kept.
     *
     * The comparison is deliberately wider than the number itself, and by more
     * than the one ring that would make it exactly equal.
     *
     * What this must never be is tighter than the game's own decision, because
     * the two are not symmetrical. A chunk the game wanted and this refuses is
     * not late, it is lost: nothing records that it went unsent, and the test
     * that would offer it again only flips when the player moves far enough for
     * that chunk to leave his rectangle and come back. It shows up as a hole
     * that heals only when he changes his distance and everything is re-sent.
     * A chunk the game did not want and this lets through is one he keeps and
     * nobody misses.
     *
     * So it is not asked to be accurate, only to be gross. It exists for one
     * case: a guest on a real connection who asked for four while the server
     * serves thirty-two, and who would otherwise be sent every chunk that
     * finishes anywhere near him. Refusing past twice his own radius takes
     * nearly all of that away and leaves the game's own edge - which reaches
     * one ring past the number, measured from a position this reads separately
     * and a moment later - far inside what is allowed.
     *
     * Anything tighter has been tried and is wrong. Two rings of slack still
     * put a host at eight into empty ground while his guest played at
     * thirty-two, and the same world was faultless the moment the two numbers
     * matched and this stopped narrowing anything at all.
     *
     * It is also worth saying what this never saves: the host's own chunks go
     * to a client inside the same process. Being generous here costs him
     * nothing whatever.
     */
    public static boolean shouldSend(Object chunkMap, Object player, Object chunk) {
        try {
            int radius = radiusFor(chunkMap, player);
            if (radius <= 0) {
                return true;
            }
            Object chunkPos = CHUNK_POSITION_OF.method(chunk).invoke(chunk);
            Object playerPos = PLAYER_CHUNK_POSITION.method(player).invoke(player);
            int dx = Math.abs(CHUNK_POS_X.field(chunkPos).getInt(chunkPos)
                - CHUNK_POS_X.field(playerPos).getInt(playerPos));
            int dz = Math.abs(CHUNK_POS_Z.field(chunkPos).getInt(chunkPos)
                - CHUNK_POS_Z.field(playerPos).getInt(playerPos));
            return Math.max(dx, dz) <= radius * 2 + 2;
        } catch (Throwable exception) {
            return true;
        }
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
                    // What he wants, which is not always what he is holding: a
                    // change that arrived inside the re-track floor is still
                    // waiting its turn. How far the world is loaded should
                    // follow what people asked for rather than what the
                    // bookkeeping has caught up with, and it matters most
                    // downwards - a guest who drops from thirty-two to four
                    // should stop costing the host a thirty-two chunk world at
                    // once, not when the tracking next gets round to him.
                    asked = PENDING.get(who);
                    if (asked == null) {
                        asked = ASKED.get(who);
                    }
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
        Object value = PLAYER_UUID.method(player).invoke(player);
        return value instanceof UUID ? (UUID) value : null;
    }

    /**
     * The number the server is serving, read from the field the patch replaced
     * the reading of. Negative where it cannot be had, which the caller reads
     * as "leave it alone".
     */
    private static int serverRadius(Object chunkMap) {
        try {
            int read = CHUNK_VIEW_DISTANCE.field(chunkMap).getInt(chunkMap);
            if (read > 0) {
                lastGoodRadius = read;
            }
            return read;
        } catch (Throwable exception) {
            if (!complainedAboutRadius) {
                complainedAboutRadius = true;
                System.out.println(
                    "[PortableIdentity] the server's view distance could not be read, so every player is "
                        + "served the last number that could be: " + exception);
            }
            return -1;
        }
    }

    /**
     * One member of one class, found the first time it is wanted and kept.
     *
     * The launcher writes both spellings of every name into a system property -
     * the one this runtime loads and the obfuscated one - and those properties
     * are set before the game starts and never change, so the list is split
     * once as well.
     *
     * What is kept is checked before it is used: a member found on one class is
     * only handed back for an object that class would accept. That is what
     * makes it safe to keep a single copy for a name that several classes might
     * answer to, and it is how the same holder serves a LevelChunk and a
     * ProtoChunk without noticing the difference.
     */
    private static final class Member {
        private final String property;
        private final String[] fallbacks;
        private volatile Method method;
        private volatile Field field;

        private Member(String property, String... fallbacks) {
            this.property = property;
            this.fallbacks = fallbacks;
        }

        private Method method(Object target) throws ReflectiveOperationException {
            Method known = this.method;
            if (known != null && known.getDeclaringClass().isInstance(target)) {
                return known;
            }
            Method found = search(target.getClass());
            this.method = found;
            return found;
        }

        private Field field(Object target) throws ReflectiveOperationException {
            Field known = this.field;
            if (known != null && known.getDeclaringClass().isInstance(target)) {
                return known;
            }
            Field found = fieldOf(target.getClass());
            this.field = found;
            return found;
        }

        private Method search(Class<?> from) throws NoSuchMethodException {
            String[] names = aliases(property, fallbacks);
            for (Class<?> type = from; type != null; type = type.getSuperclass()) {
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
            // A loader may have left the name on an interface instead; the
            // public view of the class carries what it inherited from one.
            for (String name : names) {
                try {
                    return from.getMethod(name);
                } catch (NoSuchMethodException ignored) {
                    // Try the next spelling.
                }
            }
            throw new NoSuchMethodException(property);
        }

        private Field fieldOf(Class<?> from) throws NoSuchFieldException {
            String[] names = aliases(property, fallbacks);
            for (Class<?> type = from; type != null; type = type.getSuperclass()) {
                for (String name : names) {
                    try {
                        Field candidate = type.getDeclaredField(name);
                        candidate.setAccessible(true);
                        return candidate;
                    } catch (NoSuchFieldException ignored) {
                        // Keep walking: a loader may have moved it up a class.
                    }
                }
            }
            throw new NoSuchFieldException(property);
        }
    }

    /**
     * The name lists come from system properties that are set before the game
     * starts and never change afterwards, so each is split once. They are read
     * on paths that run thousands of times a tick.
     */
    private static final Map<String, String[]> ALIASES = new ConcurrentHashMap<>();

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
