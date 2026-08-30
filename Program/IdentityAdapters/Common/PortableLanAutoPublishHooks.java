package minecraft.portable.identity;

import java.util.Collections;
import java.util.Map;
import java.util.WeakHashMap;

public final class PortableLanAutoPublishHooks {
    /** The game's own maximum view distance, and now the ceiling for a shared world. */
    private static final int SERVE_DISTANCE_CHUNKS = 32;

    // One publish attempt per screen instance: Screen.resize() re-runs init() on
    // the same instance and must not re-publish or discard user edits.
    private static final Map<Object, Boolean> ATTEMPTED =
        Collections.synchronizedMap(new WeakHashMap<>());

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
                Object message = PortableIdentityReflection.invokeStatic(
                    publishCommandType,
                    new Class<?>[] { int.class },
                    new Object[] { port },
                    aliases("publishSuccessMethods", "getSuccessMessage", "a"));
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
        // Thirty-two is the game's own maximum, the number a dedicated server
        // is given by default and the highest any client's slider offers. It is
        // the ceiling here for the same reason: nobody should be held below what
        // the game itself allows because of a setting somebody else picked for
        // their own screen. What each guest actually receives is still his own
        // number, and what the host pays is the sum of those - the same bill a
        // dedicated server hands its owner.
        int chunks = SERVE_DISTANCE_CHUNKS;
        if (!hasAll("serverLevelClasses", "chunkSourceClasses", "getAllLevelsMethods",
                    "getChunkSourceMethods", "setChunkViewDistanceMethods")) {
            return;
        }

        try {
            Iterable<?> levels = (Iterable<?>) PortableIdentityReflection.invoke(
                server,
                aliases("getAllLevelsMethods", "getAllLevels", "K"));
            Runnable apply = () -> {
                try {
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
                "[PortableIdentity] LAN world served at " + chunks
                    + " chunks; this machine still draws its own number.");

            // And it is set again while the world stays open, because the
            // integrated server writes its own figure the moment the host moves
            // his render distance: it compares against what PlayerList holds,
            // which is still the host's, so any change there takes the chunk map
            // with it. Setting the same value twice costs nothing - the game
            // compares before it acts - and the thread ends with the world.
            Thread keeper = new Thread(() -> {
                try {
                    while (true) {
                        Thread.sleep(5000L);
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
