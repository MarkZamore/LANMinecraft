package minecraft.portable.identity;

import java.util.Collections;
import java.util.Map;
import java.util.WeakHashMap;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;

public final class PortableLanAutoPublishHooks {
    /**
     * As far as a shared world is ever served, whatever anybody asks for.
     *
     * The game's own maximum is thirty-two and this used to match it. It does
     * not any more, because thirty-two does not arrive: a view that wide is
     * something over a hundred megabytes of chunks, a Steam relay carries about
     * four and a half a second by this launcher's own measurements, and a
     * client gives up on a server that has sent it nothing for thirty seconds.
     * The guest asking for thirty-two was therefore timed out by the arithmetic
     * of her own request, again and again, while the host's machine spent
     * itself generating ground she would never be shown.
     *
     * Sixteen is a quarter of the chunks and fits with room to spare. Nobody
     * has to know: a client renders the smaller of its own slider and what the
     * server announces, so a player who leaves his at thirty-two simply sees
     * sixteen and keeps playing.
     */
    private static final int SERVE_DISTANCE_LIMIT = 16;

    // One publish attempt per screen instance: Screen.resize() re-runs init() on
    // the same instance and must not re-publish or discard user edits.
    private static final Map<Object, Boolean> ATTEMPTED =
        Collections.synchronizedMap(new WeakHashMap<>());

    /**
     * Raised whenever somebody other than this code moves the server's view
     * distance, so the keeper can put it back within a tick instead of within
     * five seconds.
     */
    private static final Semaphore CHANGED = new Semaphore(0);

    /** The last number this code asked the server to serve. */
    private static volatile int lastApplied = -1;

    /**
     * Called from the head of ChunkMap.setViewDistance, on whichever thread is
     * setting it - which on a host moving his own slider is the tick thread,
     * mid-method, before the field is written.
     *
     * So this does the least it possibly can: it compares and releases a
     * permit. It must not post a task to the server, because the server runs a
     * task posted from its own thread INLINE rather than queueing it, which
     * would re-enter setViewDistance underneath the frame that is still running
     * and leave the host's narrow number written last, after two full sweeps of
     * the chunk map. Waking the keeper instead means the re-apply arrives from
     * a thread the server will genuinely queue, one tick later, on a clean
     * stack.
     *
     * The comparison against what this code last applied is what stops the
     * loop: applying a number itself calls the setter once per level, and every
     * one of those calls arrives here.
     */
    static void serverDistanceChanged(int requested) {
        if (requested != lastApplied) {
            CHANGED.release();
        }
    }

    private PortableLanAutoPublishHooks() {
    }

    public static boolean autoPublish(Object screen) {
        if (ATTEMPTED.putIfAbsent(screen, Boolean.TRUE) != null) {
            return false;
        }

        try {
            ClassLoader loader = screen.getClass().getClassLoader();
            Class<?> minecraftType = loadClass(
                loader,
                aliases("minecraftClasses", "net.minecraft.client.Minecraft", "fgo"));
            Class<?> screenType = loadClass(
                loader,
                aliases("screenClasses", "net.minecraft.client.gui.screens.Screen", "fod"));
            Class<?> gameTypeType = loadClass(
                loader,
                aliases("gameTypeClasses", "net.minecraft.world.level.GameType", "dct"));
            Class<?> httpUtilType = loadClass(
                loader,
                aliases("httpUtilClasses", "net.minecraft.util.HttpUtil", "ayf"));
            Class<?> publishCommandType = loadClass(
                loader,
                aliases("publishCommandClasses", "net.minecraft.server.commands.PublishCommand", "ans"));
            Class<?> componentType = loadClass(
                loader,
                aliases("componentClasses", "net.minecraft.network.chat.Component", "wz"));
            // An unrelated default-package class can collide with the obfuscated
            // screen name; only a real Screen may trigger a publish.
            if (!screenType.isInstance(screen)) {
                return false;
            }

            Object minecraft = PortableIdentityReflection.invokeStatic(
                minecraftType,
                new Class<?>[0],
                new Object[0],
                aliases("minecraftGetInstanceMethods", "getInstance", "Q"));
            Object server = PortableIdentityReflection.invoke(
                minecraft,
                aliases("getSingleplayerServerMethods", "getSingleplayerServer", "V"));
            if (server == null) {
                return false;
            }
            if (Boolean.TRUE.equals(PortableIdentityReflection.invoke(
                server,
                aliases("isPublishedMethods", "isPublished", "r")))) {
                // Already open. The settings screen has nothing left to offer -
                // the port is taken and the access mode belongs to the mod that
                // carries the session - so the press does what the first one
                // did: nothing visible. A menu that keeps its "open to LAN"
                // button after the world is open would otherwise walk a player
                // into a screen that cannot change anything.
                closeScreenSoon(minecraftType, screenType);
                return true;
            }

            // The values the vanilla screen would pre-select: the world's own
            // game mode and command permission, plus a free port.
            Object gameType = PortableIdentityReflection.invoke(
                server,
                aliases("getDefaultGameTypeMethods", "getDefaultGameType", "u_"));
            Object worldData = PortableIdentityReflection.invoke(
                server,
                aliases("getWorldDataMethods", "getWorldData", "bb"));
            boolean commands = Boolean.TRUE.equals(PortableIdentityReflection.invoke(
                worldData,
                aliases("isAllowCommandsMethods", "isAllowCommands", "m")));
            int port = (Integer) PortableIdentityReflection.invokeStatic(
                httpUtilType,
                new Class<?>[0],
                new Object[0],
                aliases("getAvailablePortMethods", "getAvailablePort", "a"));

            // Publish before closing the screen so every failure path falls
            // through to the untouched vanilla settings UI.
            boolean published = Boolean.TRUE.equals(PortableIdentityReflection.invokeDeclared(
                server,
                new Class<?>[] { gameTypeType, boolean.class, int.class },
                new Object[] { gameType, commands, port },
                aliases("publishServerMethods", "publishServer", "a")));
            if (!published) {
                System.out.println(
                    "[PortableIdentity] LAN auto-publish could not bind port " + port +
                    "; opening the vanilla screen.");
                return false;
            }

            try {
                closeScreenSoon(minecraftType, screenType);
                sayItInChat(minecraft, publishCommandType, componentType, port);
                PortableIdentityReflection.invoke(
                    minecraft,
                    aliases("updateTitleMethods", "updateTitle", "d"));
            } catch (Throwable exception) {
                // The world is already published; suppressing the guard now
                // would let the vanilla button publish a second time.
                System.out.println(
                    "[PortableIdentity] LAN world published on port " + port +
                    " but screen cleanup failed: " + exception);
            }
            applyServeDistance(server);
            System.out.println("[PortableIdentity] LAN world auto-published on port " + port + ".");
            return true;
        } catch (Throwable exception) {
            System.out.println("[PortableIdentity] LAN auto-publish unavailable: " + exception);
            return false;
        }
    }

    /**
     * The line vanilla writes in chat when a world is published, best effort.
     *
     * <p>Not every version has it under a name this can find: on 1.18.2 the
     * lookup for getSuccessMessage throws NoSuchMethodException every single
     * launch, and it used to take the title update down with it and print an
     * exception into the log of a session where nothing had gone wrong. A
     * version that cannot say the world is published has still published it,
     * so a missing method here is silence rather than a failure.</p>
     *
     * <p>Anything else that goes wrong is still thrown, because that is a real
     * fault and the caller writes it down.</p>
     */
    private static void sayItInChat(
        Object minecraft,
        Class<?> publishCommandType,
        Class<?> componentType,
        int port) throws ReflectiveOperationException {
        Object message;
        try {
            message = PortableIdentityReflection.invokeStatic(
                publishCommandType,
                new Class<?>[] { int.class },
                new Object[] { port },
                aliases("publishSuccessMethods", "getSuccessMessage", "a"));
        } catch (NoSuchMethodException absentOnThisVersion) {
            return;
        }

        Object gui = PortableIdentityReflection.getField(
            minecraft,
            aliases("minecraftGuiFields", "gui", "l"));
        Object chat = PortableIdentityReflection.invoke(
            gui,
            aliases("guiChatMethods", "getChat", "d"));
        PortableIdentityReflection.invokeDeclared(
            chat,
            new Class<?>[] { componentType },
            new Object[] { message },
            aliases("chatAddMessageMethods", "addMessage", "a"));
    }

    /**
     * How far the world is served, which is a different question from how far
     * the host draws it.
     *
     * The integrated server settles both with one number: every tick it reads
     * the host's own render distance and, when it differs from what PlayerList
     * holds, calls setViewDistance with it. That number is then the ceiling for
     * every player - each of them still asks for less if they want, but nobody
     * can ask for more, so a host who draws twelve chunks serves twelve.
     *
     * ChunkMap keeps its own copy of the figure, and it is that copy the server
     * actually sends from. Writing to it directly leaves PlayerList holding the
     * host's number, so the comparison the server makes each tick stays true and
     * it never writes over what was set here.
     *
     * Left alone unless the launcher passes a number, which is the old
     * behaviour: the host's own distance for everybody.
     */
    private static void applyServeDistance(Object server) {
        // requestedViewDistanceMethods is not in this list: a version without
        // it is a version where the adapter keeps the numbers itself, and
        // asked() knows which of the two it is looking at.
        if (!hasAll("serverLevelClasses", "chunkSourceClasses", "getAllLevelsMethods",
                    "getChunkSourceMethods", "setChunkViewDistanceMethods",
                    "getPlayersMethods")) {
            return;
        }

        try {
            Iterable<?> levels = (Iterable<?>) PortableIdentityReflection.invoke(
                server,
                aliases("getAllLevelsMethods", "getAllLevels", "K"));
            Runnable apply = () -> {
                try {
                    // What somebody actually asked for. The server loads and
                    // sends by one figure for everybody, so setting it to the
                    // largest number anyone present wants gives each of them his
                    // own view - and gives a host playing alone exactly what he
                    // chose, rather than making him carry a room that is not
                    // there.
                    int chunks = asked(server);
                    // Nothing to say: the server's own number stands, which is
                    // what the tick would have set anyway.
                    if (chunks <= 0) {
                        return;
                    }
                    // Told first, then served. The setter hands chunks to
                    // players synchronously, inside its own loop, so a client
                    // that has not yet heard the new radius throws every one of
                    // them away - and the server has already written them down
                    // as delivered, so it will not offer them again until the
                    // player walks far enough for them to leave his rectangle
                    // and come back. That is a ring of holes in his world, not
                    // a line in a log. The game's own setter announces first
                    // for the same reason, and it is safe in both directions: a
                    // client told more than it is about to get waits with a
                    // bigger array, and one told less drops what it is about to
                    // be told to forget anyway.
                    lastApplied = chunks;
                    announceServeDistance(server, chunks);
                    for (Object level : levels) {
                        Object source = PortableIdentityReflection.invoke(
                            level,
                            aliases("getChunkSourceMethods", "getChunkSource", "l"));
                        PortableIdentityReflection.invokeDeclared(
                            source,
                            new Class<?>[] { int.class },
                            new Object[] { chunks },
                            aliases("setChunkViewDistanceMethods", "setViewDistance", "a"));
                    }
                } catch (Throwable exception) {
                    System.out.println(
                        "[PortableIdentity] LAN serve distance could not be set: " + exception);
                }
            };
            // The chunk map belongs to the server thread, and the server is its
            // own executor.
            if (!(server instanceof java.util.concurrent.Executor executor)) {
                apply.run();
                return;
            }
            executor.execute(apply);
            System.out.println(
                "[PortableIdentity] LAN world is served as far as the furthest player asks, "
                    + "up to " + SERVE_DISTANCE_LIMIT + " chunks.");

            // And it is set again while the world stays open, because the
            // integrated server writes its own figure the moment the host moves
            // his render distance: it compares against what PlayerList holds,
            // which is still the host's, so any change there takes the chunk map
            // with it. Setting the same value twice costs nothing - the game
            // compares before it acts - and the thread ends with the world.
            Thread keeper = new Thread(() -> {
                try {
                    while (true) {
                        // Woken the moment the host's own slider moves the
                        // server's number, and every five seconds regardless so
                        // that a guest arriving or leaving is noticed too.
                        CHANGED.tryAcquire(5, TimeUnit.SECONDS);
                        // A dragged slider passes through every notch on the
                        // way, and each notch costs the tick thread a sweep of
                        // the whole loaded map plus a ring of packets each way.
                        // Waiting a moment turns a drag into one re-apply.
                        Thread.sleep(150L);
                        CHANGED.drainPermits();
                        if (!Boolean.TRUE.equals(PortableIdentityReflection.invoke(
                            server,
                            aliases("isPublishedMethods", "isPublished", "r")))) {
                            return;
                        }
                        executor.execute(apply);
                    }
                } catch (InterruptedException interrupted) {
                    Thread.currentThread().interrupt();
                } catch (Throwable ignored) {
                    // The world is going or gone; nothing here is worth a line.
                }
            }, "portable-serve-distance");
            keeper.setDaemon(true);
            keeper.start();
        } catch (Throwable exception) {
            System.out.println("[PortableIdentity] LAN serve distance unavailable: " + exception);
        }
    }

    /**
     * The largest view distance anybody on this server has asked for, and never
     * more than the game's own maximum.
     *
     * The server loads and sends by a single figure, so this is the honest one:
     * with a guest on thirty-two it is thirty-two, and with nobody but the host
     * on eight it is eight. A player who has not said yet counts for nothing.
     */
    private static int asked(Object server) throws ReflectiveOperationException {
        Object playerList = PortableIdentityReflection.invoke(
            server,
            aliases("playerListMethods", "getPlayerList", "ah"));
        Object players = PortableIdentityReflection.invoke(
            playerList,
            aliases("getPlayersMethods", "getPlayers", "t"));
        int wanted = 2;
        // Before 1.20.2 the server does not remember what anybody asked for,
        // and the adapter wrote it down itself as it arrived. There is no
        // method to call on those versions, so the note is what there is.
        if (!hasAll("requestedViewDistanceMethods")) {
            int largest = PortablePerPlayerChunksHooks.largestAsked(
                players instanceof Iterable<?> present ? present : null);
            // Nobody has said anything yet, which is not the same as everybody
            // asking for nothing. Answering with the floor here would serve the
            // whole world at two chunks until somebody's settings arrived.
            return largest == 0 ? 0 : Math.min(Math.max(wanted, largest), SERVE_DISTANCE_LIMIT);
        }
        if (players instanceof Iterable<?> everyone) {
            for (Object player : everyone) {
                Object distance = PortableIdentityReflection.invoke(
                    player,
                    aliases("requestedViewDistanceMethods", "requestedViewDistance", "F"));
                if (distance instanceof Integer value) {
                    wanted = Math.max(wanted, value);
                }
            }
        }
        return Math.min(wanted, SERVE_DISTANCE_LIMIT);
    }

    /**
     * Tells every client how far the world is now served.
     *
     * A client is sent only what it was told to expect: ClientChunkCache drops
     * a chunk outside the radius it last heard, writing "Ignoring chunk since
     * it's not in the view range" to its log and nothing to the screen. That
     * number is announced by PlayerList.setViewDistance, which this code
     * deliberately does not call - the integrated server would undo it on the
     * next tick - so without this the world was served further and every chunk
     * past the host's own number was thrown away by the guest who asked for it.
     *
     * Failing here is worth saying out loud and worth nothing else: the world
     * is open either way.
     */
    private static void announceServeDistance(Object server, int chunks) {
        try {
            ClassLoader loader = server.getClass().getClassLoader();
            Class<?> radiusPacket = loadClass(
                loader,
                aliases(
                    "chunkRadiusPacketClasses",
                    "net.minecraft.network.protocol.game.ClientboundSetChunkCacheRadiusPacket",
                    "aew"));
            Class<?> packetType = loadClass(
                loader,
                aliases("networkPacketClasses", "net.minecraft.network.protocol.Packet", "zg"));
            Object packet = radiusPacket.getDeclaredConstructor(int.class).newInstance(chunks);
            Object playerList = PortableIdentityReflection.invoke(
                server,
                aliases("playerListMethods", "getPlayerList", "ah"));
            PortableIdentityReflection.invokeDeclared(
                playerList,
                new Class<?>[] { packetType },
                new Object[] { packet },
                aliases("broadcastAllMethods", "broadcastAll", "a"));
        } catch (Throwable exception) {
            System.out.println(
                "[PortableIdentity] LAN serve distance was set but not announced, so guests will " +
                "drop what they were not told to expect: " + exception);
        }
    }

    /** Whether the launcher found every name this needs in the game it started. */
    private static boolean hasAll(String... propertyNames) {
        for (String name : propertyNames) {
            String value = System.getProperty("minecraft.portable.identity." + name);
            if (value == null || value.isBlank()) {
                return false;
            }
        }
        return true;
    }

    /**
     * Puts the screen away, which is what the vanilla one does when it closes -
     * but not this instant. The screen is in the middle of being built, and
     * mods look at what it built as soon as it is done; closing it out from
     * under them is how the game went down. The client is an Executor, so the
     * close waits in its queue and runs at the top of the next frame, before
     * anything is drawn - the screen is put away without ever being seen.
     */
    private static void closeScreenSoon(Class<?> minecraftType, Class<?> screenType)
        throws ReflectiveOperationException {
        Object minecraft = PortableIdentityReflection.invokeStatic(
            minecraftType,
            new Class<?>[0],
            new Object[0],
            aliases("minecraftGetInstanceMethods", "getInstance", "Q"));
        Runnable close = () -> {
            try {
                PortableIdentityReflection.invokeDeclared(
                    minecraft,
                    new Class<?>[] { screenType },
                    new Object[] { null },
                    aliases("setScreenMethods", "setScreen", "a"));
            } catch (Throwable exception) {
                System.out.println(
                    "[PortableIdentity] LAN share screen could not be closed: " + exception);
            }
        };
        if (minecraft instanceof java.util.concurrent.Executor client) {
            client.execute(close);
        } else {
            close.run();
        }
    }

    private static Class<?> loadClass(ClassLoader loader, String... names) throws ClassNotFoundException {
        for (String name : names) {
            try {
                return Class.forName(name, true, loader);
            } catch (ClassNotFoundException ignored) {
                // Try the runtime-mapped name.
            }
        }
        throw new ClassNotFoundException(String.join("/", names));
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
}
