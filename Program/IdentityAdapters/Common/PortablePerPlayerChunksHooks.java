package minecraft.portable.identity;

import java.lang.reflect.Array;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.util.ArrayDeque;
import java.util.HashSet;
import java.util.Iterator;
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

    /**
     * What each player has been offered and not yet handed.
     *
     * From 1.20.2 the game paces this itself and none of this is installed.
     * Before that the server writes chunks to a connection as fast as it can,
     * and everything else on that connection waits behind them: the guest's
     * own actions going out, the server's answers coming back. That is eating
     * that plays and never finishes, a blow that lands late, and a guest
     * thrown back by "moved too quickly" - his client stalled on a burst of
     * chunks and then reported one long step.
     *
     * Keyed by who the player is, for the same reason ASKED is: the game
     * builds a new ServerPlayer at every respawn and every portal.
     */
    private static final Map<UUID, Pending> HELD = new ConcurrentHashMap<>();

    /**
     * How many chunks one player may be handed in one tick.
     *
     * The number is the link, not the machine. This launcher measures about
     * four and a half megabytes a second across a Steam relay, and the packs
     * it runs cost something like twenty-four kilobytes a chunk. Eight a tick
     * is a hundred and sixty a second, near four megabytes - under the link,
     * with about a seventh left over for everything that is not ground, which
     * is the entire point of the exercise.
     *
     * A full view at the launcher's own ceiling of sixteen is 33 by 33, which
     * is 1089 chunks and under seven seconds at this rate; a view of eight is
     * 289 and under two. Both are far inside the thirty seconds a client waits
     * before it decides the server is gone. Vanilla starts at nine and then
     * adapts, because its client says how fast it is keeping up; this cannot
     * ask, so it errs the other way.
     */
    private static final int CHUNKS_PER_TICK = 8;

    /**
     * How deep a hold may get before the pacing gives way and the game sends
     * as it always did. Twice a full view at the serve ceiling, so a single
     * arrival can never reach it - the one moment the feature exists for.
     * Reaching it means handing over at once, never dropping: a chunk this
     * hook drops is a hole in a world that heals only when its owner changes
     * his render distance.
     */
    private static final int HOLD_LIMIT = 2048;

    /** Whether the launcher found the names the pacing needs. */
    private static final boolean PACING =
        "true".equals(System.getProperty("minecraft.portable.identity.chunkPacingEnabled"));

    /**
     * The player whose client is in this very process. He is never paced: his
     * chunks cross no wire, so holding them back would cost him seconds of
     * trickle on every world load and buy nobody anything.
     */
    /**
     * The player sitting at this machine, or null when nobody said.
     *
     * Held as a UUID rather than as text because it is compared once per chunk
     * offered: two longs against two longs, with nothing built to do it. The
     * launcher writes the dashed spelling and this reads either.
     */
    private static final UUID LOCAL_PLAYER =
        readUuid(System.getProperty("minecraft.portable.identity.localPlayer", ""));

    /** The thread inside the pump, whose own re-offers go straight through. */
    private static volatile Thread pumping;

    /** Set where the pump cannot work at all, after which nothing is held. */
    private static volatile boolean broken;

    private static volatile Method knownDelivery;
    private static volatile boolean complainedAboutHolding;


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
        Integer holding = ASKED.get(who);
        if (server <= 0 || effective(server, pending) <= effective(server, holding)) {
            // Nothing to fetch, so the note is the whole of it. Two cases meet
            // here. One is a number that changes nothing at all. The other is a
            // player asking for less than he has, and he is the reason this
            // branch exists rather than only the first: putting him back on the
            // map would forget and re-send every chunk of the smaller ring,
            // every one of which he is already holding. That is the largest
            // burst this hook can produce, aimed at the one player who has just
            // said he wants less - and on a link where chunks are the
            // bottleneck it stalls everything he does until it drains. Asking
            // for less should cost nothing, and now it does: the note narrows
            // him at once, and the outer ring he keeps is stopped at his own
            // client, which draws no further than his own slider.
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
        PENDING.remove(who);
        retrack(chunkMap, player, who, holding, pending.intValue());
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
     * Whether the game may hand this chunk over in this very frame.
     *
     * One place in the bytecode, two questions, so one arbiter answers both.
     * The first is the older one and has not changed: is this chunk his at all,
     * by the distance he asked for. The second is the pacing: is it his turn.
     *
     * False has always meant "not in this frame" and still does - but there are
     * two reasons for it now, and one of them takes the chunk with it and hands
     * it over later. Every other answer is true, which is the game sending
     * exactly as it would with none of this installed: a player who cannot be
     * named, a delivery method that cannot be found, a scratch that cannot be
     * built, a hold already full. Nothing is ever dropped.
     */
    public static boolean admit(Object chunkMap, Object player, Object cache, Object chunk) {
        // The pump re-offering what it is about to send, having already asked
        // everything below.
        if (Thread.currentThread() == pumping) {
            return true;
        }
        if (!shouldSend(chunkMap, player, chunk)) {
            return false;
        }
        if (!PACING || broken) {
            return true;
        }
        try {
            UUID who = uuidOf(player);
            // The host plays in this very process. Pacing his own ground would
            // cost him seconds on every world he opens and save nobody
            // anything: none of it goes near the wire.
            if (who == null || who.equals(LOCAL_PLAYER)) {
                return true;
            }
            // Found before the first chunk is ever held, never at the first
            // send. A hook that takes a chunk it cannot later hand back is the
            // hole-in-the-world bug wearing a different coat.
            if (delivery(chunkMap, player, chunk) == null) {
                return true;
            }
            Object spare = freshLike(cache);
            if (spare == null) {
                return true;
            }
            long position = packed(CHUNK_POSITION_OF.method(chunk).invoke(chunk));

            Pending pending = HELD.get(who);
            if (pending == null || pending.player != player || pending.map != chunkMap) {
                // A different ServerPlayer or a different level means a
                // respawn, a portal or a dimension change, and every one of
                // those is followed at once by his whole rectangle being
                // offered again. What was held is worth nothing, and that is
                // the only condition under which throwing it away is safe.
                pending = new Pending(chunkMap, player);
                HELD.put(who, pending);
            }
            synchronized (pending) {
                if (pending.queue.size() >= HOLD_LIMIT) {
                    return true;
                }
                if (!pending.live.add(position)) {
                    // Offered twice before the pump reached it. Held once.
                    return false;
                }
                pending.queue.addLast(new Held(position, chunk, spare));
            }
            return false;
        } catch (Throwable exception) {
            return true;
        }
    }

    /**
     * Hands over what was held, a few per player, once a tick.
     *
     * The game method is called again with the arguments it was given, so the
     * packet, the entity pairings and the passenger packets it also sends are
     * all built by the game exactly as it wrote them. Nothing here reproduces
     * any of that.
     */
    public static void deliverHeld(Object chunkMap) {
        if (!PACING || broken || HELD.isEmpty()) {
            return;
        }
        Thread previous = pumping;
        pumping = Thread.currentThread();
        try {
            for (Iterator<Map.Entry<UUID, Pending>> entries = HELD.entrySet().iterator();
                 entries.hasNext();) {
                Pending pending = entries.next().getValue();
                // Players of another level wait for that level to tick.
                if (pending.map != chunkMap) {
                    continue;
                }
                synchronized (pending) {
                    int budget = CHUNKS_PER_TICK;
                    while (budget > 0 && !pending.queue.isEmpty()) {
                        Held held = pending.queue.pollFirst();
                        // The server has since told him to forget it. Handing
                        // it over now would leave a chunk on his client that
                        // nothing will ever tell him to drop again.
                        if (!pending.live.remove(held.position)) {
                            continue;
                        }
                        // Or he walked away from it. The same refusal the offer
                        // itself would have made, only later, and safe the same
                        // way: walking back flips the game own test and offers
                        // it again.
                        if (!shouldSend(chunkMap, pending.player, held.chunk)) {
                            continue;
                        }
                        if (!hand(chunkMap, pending.player, held)) {
                            return;
                        }
                        budget--;
                    }
                    if (pending.queue.isEmpty()) {
                        entries.remove();
                    }
                }
            }
        } catch (Throwable exception) {
            // A pump that throws would stop the tick. It gives up instead, and
            // everything after it goes out the way the game sends it.
            broken = true;
            System.out.println("[PortableIdentity] chunks are no longer paced: " + exception);
        } finally {
            pumping = previous;
        }
    }

    /**
     * Called where the server tells a client to forget a chunk, so that one
     * still waiting is not handed over afterwards.
     */
    public static void cancelHeld(Object player, Object chunkPos) {
        if (!PACING || HELD.isEmpty()) {
            return;
        }
        try {
            UUID who = uuidOf(player);
            if (who == null) {
                return;
            }
            Pending pending = HELD.get(who);
            if (pending == null) {
                return;
            }
            long position = packed(chunkPos);
            synchronized (pending) {
                pending.live.remove(position);
            }
        } catch (Throwable exception) {
            // Then it goes out, which is what would have happened anyway.
        }
    }

    /** One held chunk, handed to the game. True while the pump still works. */
    private static boolean hand(Object chunkMap, Object player, Held held) {
        Method how = delivery(chunkMap, player, held.chunk);
        if (how == null) {
            // Nothing was held without this being found first, so this is a
            // map that has changed underfoot. The chunk is dropped rather than
            // sent to the wrong place, and the next offer of it goes straight
            // out because the same lookup fails there too.
            return true;
        }
        try {
            how.invoke(chunkMap, player, held.cache, held.chunk);
            return true;
        } catch (InvocationTargetException thrown) {
            // The game own code threw, as it would have at the call this stands
            // in for. One chunk lost, the pump unharmed.
            complainOnce("a held chunk could not be handed over: " + thrown.getCause());
            return true;
        } catch (Throwable exception) {
            broken = true;
            System.out.println(
                "[PortableIdentity] chunks are no longer paced, they go out as the game sends them: "
                    + exception);
            return false;
        }
    }

    /**
     * An empty one of whatever the game passed as its scratch argument: an
     * array of the same length before 1.18, a MutableObject after it.
     *
     * The one the caller passed must not be kept. The game makes a single
     * scratch and shares it across every player the chunk is offered to, so
     * that the packet is built once - holding it would take an object somebody
     * else still owns, and would hand the chunk over as it was when it was
     * offered rather than as it is when it arrives. Block changes in between
     * are broadcast separately and thrown away by a client that has no chunk to
     * put them in, so a stale packet is ground that arrives already wrong.
     * Vanilla builds its packet at the moment of sending too.
     */
    private static Object freshLike(Object cache) {
        try {
            Class<?> type = cache.getClass();
            if (type.isArray()) {
                return Array.newInstance(type.getComponentType(), Array.getLength(cache));
            }
            return type.getDeclaredConstructor().newInstance();
        } catch (Throwable exception) {
            complainOnce("chunks cannot be paced on this Minecraft: " + exception);
            return null;
        }
    }

    /**
     * The method that hands one chunk to one player, found by name and by the
     * shape of the things in hand rather than by types in the abstract - the
     * middle argument changed type at 1.18 and is never named here.
     */
    private static Method delivery(Object chunkMap, Object player, Object chunk) {
        Method known = knownDelivery;
        if (known != null && known.getDeclaringClass().isInstance(chunkMap)) {
            return known;
        }
        String[] names = aliases("playerLoadedChunkMethods", "playerLoadedChunk", "a");
        for (Class<?> type = chunkMap.getClass(); type != null; type = type.getSuperclass()) {
            for (Method candidate : type.getDeclaredMethods()) {
                Class<?>[] parameters = candidate.getParameterTypes();
                if (parameters.length != 3 ||
                    !parameters[0].isInstance(player) ||
                    !parameters[2].isInstance(chunk)) {
                    continue;
                }
                for (String name : names) {
                    if (candidate.getName().equals(name)) {
                        candidate.setAccessible(true);
                        knownDelivery = candidate;
                        return candidate;
                    }
                }
            }
        }
        complainOnce("the way to hand a chunk over could not be found, so nothing is paced");
        return null;
    }

    /** A UUID written either way, or null if it was written no way at all. */
    private static UUID readUuid(String text) {
        try {
            String plain = text.replace("-", "").trim();
            if (plain.length() != 32) {
                return null;
            }
            return new UUID(
                Long.parseUnsignedLong(plain.substring(0, 16), 16),
                Long.parseUnsignedLong(plain.substring(16), 16));
        } catch (Throwable exception) {
            return null;
        }
    }

    /** One chunk position as one number, so a hold can be searched by it. */
    private static long packed(Object chunkPos) throws ReflectiveOperationException {
        long x = CHUNK_POS_X.field(chunkPos).getInt(chunkPos) & 0xffffffffL;
        long z = CHUNK_POS_Z.field(chunkPos).getInt(chunkPos) & 0xffffffffL;
        return (z << 32) | x;
    }

    /** Said once: this sits on a path that runs for every chunk of every view. */
    private static void complainOnce(String what) {
        if (complainedAboutHolding) {
            return;
        }
        complainedAboutHolding = true;
        System.out.println("[PortableIdentity] " + what);
    }

    /** One player held chunks, and the level and player they were meant for. */
    private static final class Pending {
        private final Object map;
        private final Object player;
        private final ArrayDeque<Held> queue = new ArrayDeque<>();
        /** The positions still wanted, so a cancellation costs one lookup. */
        private final HashSet<Long> live = new HashSet<>();

        private Pending(Object map, Object player) {
            this.map = map;
            this.player = player;
        }
    }

    /** One chunk waiting its turn, with the scratch the game will build into. */
    private static final class Held {
        private final long position;
        private final Object chunk;
        private final Object cache;

        private Held(long position, Object chunk, Object cache) {
            this.position = position;
            this.chunk = chunk;
            this.cache = cache;
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
