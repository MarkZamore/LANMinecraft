package minecraft.portable.identity;

import java.lang.ref.WeakReference;
import java.lang.reflect.*;
import java.util.*;
import java.util.concurrent.*;

/** No game mod, server-list file, endpoint token or additional Steam client. */
public final class PortableSteamDiscoveryHooks {
    private static final String PREFIX = "portable-steam-";
    private static final Map<Object, Poll> POLLS = Collections.synchronizedMap(new WeakHashMap<>());
    private static final Map<Object, World> DISPLAY_ENTRIES = Collections.synchronizedMap(new WeakHashMap<>());
    private static final Map<Object, Boolean> PUBLICATIONS = Collections.synchronizedMap(new WeakHashMap<>());
    private static final String PLAYER_KEY = "lanminecraft_player";
    private static final String WORLD_KEY = "lanminecraft_world";
    private static final String DISPLAY_KEY = "lanminecraft_display";

    private PortableSteamDiscoveryHooks() { }

    public static void begin(Object screen) {
        synchronized (POLLS) {
            Poll existing = POLLS.get(screen);
            if (existing != null) { existing.apply(existing.worlds); return; } // resize may replace its LAN list
            try {
                Poll poll = new Poll(screen);
                POLLS.put(screen, poll);
                poll.apply(Map.of());
                if (poll.active) poll.thread.start();
            } catch (ClassNotFoundException absentTransport) {
                // Ordinary use without e4steam keeps vanilla multiplayer.
            } catch (Throwable failure) {
                report("could not start", failure);
            }
        }
    }

    public static void end(Object screen) {
        Poll poll = POLLS.remove(screen);
        if (poll != null) {
            poll.active = false;
            poll.thread.interrupt();
        }
    }

    /** The mapped UDP insertion path stays Steam-only across screen lifetimes.
     * Saved/manual servers use the independent ServerList and never enter here.
     */
    public static boolean nativeDiscoveryManaged(Object list) {
        return Boolean.getBoolean("minecraft.portable.identity.steamDiscoveryEnabled");
    }

    /** Preserve native labels everywhere except the precise synthetic entry we created. */
    public static Object worldTitle(Object original, Object lanServer) {
        World world = DISPLAY_ENTRIES.get(lanServer);
        if (world == null) return original;
        try {
            ClassLoader loader = lanServer.getClass().getClassLoader();
            Class<?> component = null;
            for (String type : aliases("steamDiscoveryComponentClasses")) {
                try { component = Class.forName(type.replace('/', '.'), true, loader); break; }
                catch (ClassNotFoundException absentAlias) { }
            }
            if (component == null) throw new ClassNotFoundException("Native component");
            for (String name : aliases("steamDiscoveryComponentLiteralMethods")) {
                try { return component.getDeclaredMethod(name, String.class).invoke(null, world.world); }
                catch (NoSuchMethodException absentAlias) { }
            }
            for (String type : aliases("steamDiscoveryLiteralComponentClasses")) {
                if (type.isBlank()) continue;
                try { return Class.forName(type.replace('/', '.'), true, loader).getDeclaredConstructor(String.class).newInstance(world.world); }
                catch (ClassNotFoundException absentAlias) { }
            }
            throw new NoSuchMethodException("Native literal component");
        } catch (ReflectiveOperationException failure) {
            throw new IllegalStateException("Steam world title could not be built", failure);
        }
    }

    /** Only the display call site changes; joining still receives the real synthetic address. */
    public static String displayAddress(String original, Object lanServer) {
        return DISPLAY_ENTRIES.containsKey(lanServer) ? "Steam" : original;
    }

    /** Preserve the native return value even if the optional announcement cannot start. */
    public static boolean publicationResult(boolean success, Object minecraft, Object server, int port) {
        if (success) {
            try {
                published(minecraft, server, port);
            } catch (RuntimeException failure) {
                report("could not start the live world description", failure);
            }
        }
        return success;
    }

    /** Capture the actual live world, never a startup folder or parsed MOTD. No UI/Steam wait. */
    public static void published(Object minecraft, Object server, int port) {
        if (!Boolean.getBoolean("minecraft.portable.identity.steamDiscoveryEnabled") ||
            PUBLICATIONS.putIfAbsent(server, Boolean.TRUE) != null) return;
        Thread worker = new Thread(() -> publishDescription(minecraft, server, port), "portable-steam-world-description");
        worker.setDaemon(true);
        worker.start();
    }

    private static void publishDescription(Object minecraft, Object server, int port) {
        try {
            ClassLoader loader = minecraft.getClass().getClassLoader();
            Object runtime = invokeStatic(Class.forName("link.e4steam.steam.SteamRuntime", true, loader), "get");
            requireTransport(runtime);
            CompletableFuture<String[]> capture = new CompletableFuture<>();
            ((Executor) minecraft).execute(() -> {
                try {
                    if (invoke(minecraft, aliases("getSingleplayerServerMethods")) != server) { capture.complete(null); return; }
                    Object user = invoke(minecraft, aliases("steamDiscoveryGetUserMethods"));
                    String player = cleanName((String) invoke(user, aliases("steamDiscoveryUserNameMethods")));
                    ((Executor) server).execute(() -> {
                        try {
                            Object data = invoke(server, aliases("getWorldDataMethods"));
                            capture.complete(new String[] { player, cleanName((String) invoke(data, aliases("steamDiscoveryLevelNameMethods"))) });
                        } catch (Throwable failure) { capture.completeExceptionally(failure); }
                    });
                } catch (Throwable failure) { capture.completeExceptionally(failure); }
            });
            String[] description = capture.get(8, TimeUnit.SECONDS);
            if (description == null) return;
            invoke(runtime, "awaitReady");
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(90);
            while (System.nanoTime() < deadline) {
                // Bind to one real e4steam hosting generation, then recheck the
                // client before writing. A replacement world on the same port
                // must not inherit the previous world's metadata.
                Object owner = steamTask(runtime, () -> {
                    Object host = field(runtime, "hostRegistration");
                    return host != null && ((Integer) invoke(host, "localPort")) == port ? invoke(host, "owner") : null;
                }).get(8, TimeUnit.SECONDS);
                CompletableFuture<Boolean> current = new CompletableFuture<>();
                ((Executor) minecraft).execute(() -> {
                    try { current.complete(invoke(minecraft, aliases("getSingleplayerServerMethods")) == server); }
                    catch (Throwable failure) { current.completeExceptionally(failure); }
                });
                if (!current.get(8, TimeUnit.SECONDS)) return;
                if (owner != null && Boolean.TRUE.equals(steamTask(runtime, () -> {
                    Object host = field(runtime, "hostRegistration");
                    Object social = field(runtime, "lobbyManager");
                    if (host == null || invoke(host, "owner") != owner || social == null || field(social, "hostLobbyOwner") != owner) return false;
                    long lobbyId = (Long) field(social, "hostLobbyId");
                    if (lobbyId == 0) return false;
                    Object lobby = invokeStatic(Class.forName("com.codedisaster.steamworks.SteamID", true, loader), "createFromNativeHandle", lobbyId);
                    Object matchmaking = field(social, "matchmaking");
                    return Boolean.TRUE.equals(invoke(matchmaking, "setLobbyData", lobby, PLAYER_KEY, description[0])) &&
                        Boolean.TRUE.equals(invoke(matchmaking, "setLobbyData", lobby, WORLD_KEY, description[1])) &&
                        Boolean.TRUE.equals(invoke(matchmaking, "setLobbyData", lobby, DISPLAY_KEY, "1"));
                }).get(8, TimeUnit.SECONDS))) return;
                Thread.sleep(500);
            }
            throw new TimeoutException("Steam host lobby description was not ready");
        } catch (Throwable failure) { report("could not publish the live world description", failure); }
    }

    @SuppressWarnings("unchecked")
    private static CompletableFuture<Object> steamTask(Object runtime, Callable<Object> action) throws ReflectiveOperationException {
        return (CompletableFuture<Object>) invoke(runtime, "submitSteamTask", action);
    }

    public static CompletableFuture<Object> openRecentPlayers(ClassLoader loader) throws ReflectiveOperationException {
        Object runtime = invokeStatic(Class.forName("link.e4steam.steam.SteamRuntime", true, loader), "get");
        return steamTask(runtime, () -> {
            requireTransport(runtime, "pauseLegacyTransportVersion");
            Object utils = field(runtime, "utils");
            Object manager = field(runtime, "lobbyManager");
            if (utils == null || manager == null || !Boolean.TRUE.equals(invoke(utils, "isOverlayEnabled")))
                throw new IllegalStateException("Steam overlay is disabled.");
            Object friends = field(manager, "friends");
            Class<?> dialog = Class.forName("com.codedisaster.steamworks.SteamFriends$OverlayDialog", true, loader);
            Object players = dialog.getField("Players").get(null);
            invoke(friends, "activateGameOverlay", players);
            return null;
        });
    }

    private static void requireTransport(Object runtime) throws java.io.IOException {
        requireTransport(runtime, "steamDiscoveryTransportVersion");
    }

    private static void requireTransport(Object runtime, String versionProperty) throws java.io.IOException {
        Properties version = new Properties();
        try (java.io.InputStream input = runtime.getClass().getResourceAsStream("/e4steam-version.properties")) {
            if (input == null) throw new IllegalStateException("Missing pinned Steam transport version.");
            version.load(input);
        }
        if (!version.getProperty("version", "").equals(System.getProperty("minecraft.portable.identity." + versionProperty, "")))
            throw new IllegalStateException("Unsupported Steam transport discovery interface.");
    }

    public static boolean join(Object screen, Object data) {
        boolean steamAddress = false;
        try {
            String address = (String) field(data, aliases("steamDiscoveryServerAddressFields"));
            if (!address.startsWith(PREFIX)) return false;
            steamAddress = true;
            Poll poll = POLLS.get(screen);
            World world = poll == null ? null : poll.worlds.get(address);
            if (world != null && poll.active) {
                // The author's join path verifies friendship, lobby owner,
                // protocol and the private endpoint before it opens a bridge.
                // Listing a world does not join it or open a socket.
                poll.submit(() -> {
                    Object social = field(poll.runtime, "lobbyManager");
                    if (social != null) {
                        Class<?> idType = poll.load("com.codedisaster.steamworks.SteamID");
                        Object lobby = invokeStatic(idType, "createFromNativeHandle", world.lobby);
                        Object friend = invokeStatic(idType, "createFromNativeHandle", world.friend);
                        invoke(social, "requestJoin", lobby, friend, false);
                    }
                    return null;
                });
            }
            return true; // stale synthetic entries must never reach DNS
        } catch (Throwable failure) {
            report("join failed", failure);
            return steamAddress;
        }
    }

    private record World(long lobby, long friend, String player, String world) {
        String address() { return PREFIX + Long.toUnsignedString(lobby) + ".invalid"; }
    }

    private static final class Poll implements Runnable {
        final WeakReference<Object> screen;
        final ClassLoader loader;
        final Object runtime;
        final Executor client;
        final Thread thread;
        volatile boolean active = true;
        volatile Map<String, World> worlds = Map.of();

        Poll(Object screen) throws Exception {
            this.screen = new WeakReference<>(screen);
            loader = screen.getClass().getClassLoader();
            runtime = invokeStatic(load("link.e4steam.steam.SteamRuntime"), "get");
            requireTransport(runtime);
            Object minecraft = invokeStatic(loadAliases("minecraftClasses"), aliases("minecraftGetInstanceMethods"));
            client = (Executor) minecraft;
            thread = new Thread(this, "portable-steam-world-discovery");
            thread.setDaemon(true);
        }

        Class<?> load(String name) throws ClassNotFoundException { return Class.forName(name, true, loader); }

        Class<?> loadAliases(String property) throws ClassNotFoundException {
            for (String name : aliases(property)) {
                try { return load(name.replace('/', '.')); } catch (ClassNotFoundException ignored) { }
            }
            throw new ClassNotFoundException(property);
        }

        @SuppressWarnings("unchecked")
        CompletableFuture<Object> submit(Callable<Object> action) throws ReflectiveOperationException {
            return (CompletableFuture<Object>) invoke(runtime, "submitSteamTask", action);
        }

        @Override public void run() {
            boolean reported = false;
            while (active && screen.get() != null) {
                try {
                    // e4steam owns the screen's Steam activity and releases
                    // it on removed(). No wait or native call uses the UI.
                    invoke(runtime, "awaitReady");
                    @SuppressWarnings("unchecked")
                    Map<String, World> found = (Map<String, World>) submit(this::readWorlds).get(8, TimeUnit.SECONDS);
                    if (!found.equals(worlds)) {
                        worlds = found;
                        client.execute(() -> apply(found));
                    }
                    reported = false;
                } catch (InterruptedException canceled) {
                    Thread.currentThread().interrupt();
                    return;
                } catch (Throwable failure) {
                    if (!active || Thread.currentThread().isInterrupted()) return;
                    if (!reported) report("temporarily unavailable; retrying", failure);
                    reported = true;
                    if (!worlds.isEmpty()) {
                        worlds = Map.of();
                        client.execute(() -> apply(Map.of()));
                    }
                }
                try { Thread.sleep(3000); }
                catch (InterruptedException canceled) { Thread.currentThread().interrupt(); return; }
            }
        }

        Object readWorlds() throws ReflectiveOperationException {
            Map<String, World> found = new LinkedHashMap<>();
            if (!active) return found;
            Object social = field(runtime, "lobbyManager");
            if (social == null) return found;
            Object friends = field(social, "friends");
            Object matchmaking = field(social, "matchmaking");
            Object immediate = load("com.codedisaster.steamworks.SteamFriends$FriendFlags").getField("Immediate").get(null);
            int count = Math.min(2048, (Integer) invoke(friends, "getFriendCount", immediate));
            Class<?> idType = load("com.codedisaster.steamworks.SteamID");
            String protocol = String.valueOf(staticField(load("link.e4steam.steam.SteamProtocol"), "VERSION"));
            String minecraft = System.getProperty("minecraft.portable.identity.steamDiscoveryMinecraftVersion", "");
            for (int index = 0; active && index < count; index++) {
                Object friend = invoke(friends, "getFriendByIndex", index, immediate);
                if (friend == null) continue;
                invoke(friends, "requestFriendRichPresence", friend);
                String connect = (String) invoke(friends, "getFriendRichPresence", friend, "connect");
                long lobbyId = lobbyId(connect);
                if (lobbyId == 0) continue;
                Object lobby = invokeStatic(idType, "createFromNativeHandle", lobbyId);
                invoke(matchmaking, "requestLobbyData", lobby);
                if (!protocol.equals(invoke(matchmaking, "getLobbyData", lobby, "e4steam_protocol")) ||
                    !minecraft.equals(invoke(matchmaking, "getLobbyData", lobby, "e4steam_minecraft"))) continue;
                Object owner = invoke(matchmaking, "getLobbyOwner", lobby);
                long friendId = nativeId(friend);
                if (owner == null || nativeId(owner) != friendId) continue;
                if (!"1".equals(invoke(matchmaking, "getLobbyData", lobby, DISPLAY_KEY))) continue;
                String player = (String) invoke(matchmaking, "getLobbyData", lobby, PLAYER_KEY);
                String worldName = (String) invoke(matchmaking, "getLobbyData", lobby, WORLD_KEY);
                if (player == null || player.isBlank() || worldName == null || worldName.isBlank()) continue;
                World world = new World(lobbyId, friendId, cleanName(player), cleanName(worldName));
                found.put(world.address(), world);
            }
            return Collections.unmodifiableMap(found);
        }

        @SuppressWarnings("unchecked")
        void apply(Map<String, World> found) {
            Object current = screen.get();
            if (!active || current == null || POLLS.get(current) != this) return;
            try {
                Object list = field(current, aliases("steamDiscoveryLanListFields"));
                if (list == null) return;
                Class<?> entryType = loadAliases("steamDiscoveryLanServerClasses");
                Constructor<?> entry = entryType.getDeclaredConstructor(String.class, String.class);
                synchronized (list) {
                    List<Object> servers = (List<Object>) field(list, aliases("steamDiscoveryLanServersFields"));
                    // This is only the transient auto-discovery collection;
                    // saved/manual servers live in the independent ServerList.
                    servers.clear();
                    for (World world : found.values()) {
                        Object value = entry.newInstance(world.player, world.address());
                        DISPLAY_ENTRIES.put(value, world);
                        servers.add(value);
                    }
                    setField(list, true, aliases("steamDiscoveryLanDirtyFields"));
                }
            } catch (Throwable failure) {
                report("could not update multiplayer list", failure);
                active = false;
                POLLS.remove(current, this);
            }
        }
    }

    static long lobbyId(String connect) {
        if (connect == null || !connect.matches("e4steam-lobby:[1-9][0-9]{0,19}")) return 0;
        try { return Long.parseUnsignedLong(connect.substring("e4steam-lobby:".length())); }
        catch (NumberFormatException invalid) { return 0; }
    }

    static String cleanName(String name) {
        if (name == null) return "Minecraft";
        String clean = name.replaceAll("[\\p{Cc}\\p{Cf}§]", "").trim();
        return clean.isEmpty() ? "Minecraft" : clean.substring(0,
            clean.offsetByCodePoints(0, Math.min(64, clean.codePointCount(0, clean.length()))));
    }

    private static long nativeId(Object value) throws ReflectiveOperationException {
        // getNativeHandle is a static method on SteamNativeHandle.
        Class<?> type = Class.forName("com.codedisaster.steamworks.SteamNativeHandle", true, value.getClass().getClassLoader());
        return (Long) invokeStatic(type, "getNativeHandle", value);
    }

    private static Object invoke(Object target, String name, Object... args) throws ReflectiveOperationException {
        return method(target.getClass(), new String[] { name }, args).invoke(target, args);
    }

    private static Object invoke(Object target, String[] names) throws ReflectiveOperationException {
        return method(target.getClass(), names, new Object[0]).invoke(target);
    }

    private static Object invokeStatic(Class<?> type, String name, Object... args) throws ReflectiveOperationException {
        return method(type, new String[] { name }, args).invoke(null, args);
    }

    private static Object invokeStatic(Class<?> type, String[] names) throws ReflectiveOperationException {
        return method(type, names, new Object[0]).invoke(null);
    }

    private static Method method(Class<?> type, String[] names, Object[] args) throws NoSuchMethodException {
        for (String name : names) for (Class<?> current = type; current != null; current = current.getSuperclass())
            for (Method method : current.getDeclaredMethods()) {
                if (!method.getName().equals(name) || method.getParameterCount() != args.length) continue;
                Class<?>[] parameters = method.getParameterTypes();
                boolean fits = true;
                for (int index = 0; index < args.length; index++) {
                    Class<?> parameter = parameters[index];
                    if (parameter == int.class) parameter = Integer.class;
                    else if (parameter == long.class) parameter = Long.class;
                    else if (parameter == boolean.class) parameter = Boolean.class;
                    if (args[index] != null && !parameter.isInstance(args[index])) fits = false;
                }
                if (fits) { method.setAccessible(true); return method; }
            }
        throw new NoSuchMethodException(type.getName() + "." + String.join("/", names));
    }

    private static Field findField(Class<?> type, String... names) throws NoSuchFieldException {
        for (String name : names) for (Class<?> current = type; current != null; current = current.getSuperclass()) {
            try { Field field = current.getDeclaredField(name); field.setAccessible(true); return field; }
            catch (NoSuchFieldException ignored) { }
        }
        throw new NoSuchFieldException(type.getName());
    }

    private static Object field(Object target, String... names) throws ReflectiveOperationException {
        return findField(target.getClass(), names).get(target);
    }

    private static Object staticField(Class<?> type, String name) throws ReflectiveOperationException {
        return findField(type, name).get(null);
    }

    private static void setField(Object target, Object value, String... names) throws ReflectiveOperationException {
        findField(target.getClass(), names).set(target, value);
    }

    private static String[] aliases(String name) {
        return System.getProperty("minecraft.portable.identity." + name, "").split(",");
    }

    private static void report(String action, Throwable failure) {
        // Never log rich-presence strings, lobby identifiers or endpoint tokens.
        System.out.println("[PortableIdentity] Steam world discovery " + action + " (" + failure.getClass().getSimpleName() + ").");
    }
}
