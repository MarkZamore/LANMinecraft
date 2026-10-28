package minecraft.portable.identity;

import java.lang.ref.WeakReference;
import java.lang.reflect.*;
import java.util.*;
import java.util.concurrent.*;

/** No game mod, server-list file, endpoint token or additional Steam client. */
public final class PortableSteamDiscoveryHooks {
    private static final String PREFIX = "portable-steam-";
    private static final Map<Object, Poll> POLLS = Collections.synchronizedMap(new WeakHashMap<>());

    private PortableSteamDiscoveryHooks() { }

    public static void begin(Object screen) {
        synchronized (POLLS) {
            if (POLLS.containsKey(screen)) return; // resize re-enters init
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

    private record World(long lobby, long friend, String name) {
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
            Properties version = new Properties();
            try (java.io.InputStream input = runtime.getClass().getResourceAsStream("/e4steam-version.properties")) {
                if (input == null) throw new IllegalStateException("Missing pinned Steam transport version.");
                version.load(input);
            }
            if (!version.getProperty("version", "").equals(
                System.getProperty("minecraft.portable.identity.steamDiscoveryTransportVersion", "")))
                throw new IllegalStateException("Unsupported Steam transport discovery interface.");
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
                String name = (String) invoke(friends, "getFriendPersonaName", friend);
                World world = new World(lobbyId, friendId, cleanName(name));
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
                    for (Iterator<Object> iterator = servers.iterator(); iterator.hasNext();) {
                        Object server = iterator.next();
                        String address = (String) invoke(server, aliases("steamDiscoveryLanAddressMethods"));
                        if (address.startsWith(PREFIX)) iterator.remove();
                    }
                    for (World world : found.values()) servers.add(entry.newInstance("Steam · " + world.name, world.address()));
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
        String clean = name.replaceAll("[\\p{Cntrl}§]", "").trim();
        return clean.isEmpty() ? "Minecraft" : clean.substring(0, Math.min(64, clean.length()));
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
