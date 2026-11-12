package lanminecraft.qa;

import java.io.File;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardOpenOption;
import java.security.MessageDigest;
import java.util.List;
import java.util.TreeMap;
import java.util.UUID;
import java.util.concurrent.Callable;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.TimeUnit;

/** Native Java 8 test for Forge 1.12.2. No transformer, OS input or UI automation. */
public final class Forge112SteamQaAgent {
    private static final String PREFIX = "lanminecraft.steamqa.";
    private static final String MC = "net.minecraft.client.Minecraft";
    private static final String GUI = "net.minecraft.client.gui.";
    private static final Class<?>[] NONE = new Class<?>[0];
    private static final ConcurrentLinkedQueue<Runnable> CLIENT_TICK_TASKS = new ConcurrentLinkedQueue<>();
    private final Path game;
    private final Path evidence;
    private final Path world;
    private final String worldName;
    private final String reopenLevelSha256;
    private Object client;
    private ClassLoader loader;

    private Forge112SteamQaAgent() throws Exception {
        Path run = path("run");
        game = path("game");
        evidence = path("evidence").resolve("native-steam-qa-112.jsonl");
        Path installation = run.resolve("runtime-qa");
        String suffix = System.getProperty(PREFIX + "worldSuffix", "");
        reopenLevelSha256 = System.getProperty(PREFIX + "reopenLevelSha256", "");
        require(suffix.matches("[A-Za-z0-9_-]{0,16}"), "Invalid QA world suffix.");
        require(reopenLevelSha256.isEmpty() || reopenLevelSha256.matches("[0-9a-f]{64}"),
                "Invalid QA level pin.");
        worldName = "LanMcSteamQa112" + suffix;
        world = game.resolve("saves").resolve(worldName);
        require(run.getParent().getFileName().toString().equals("agent-runs")
                && Files.isRegularFile(run.resolve("RUN.json"), LinkOption.NOFOLLOW_LINKS)
                && game.startsWith(installation.resolve("Minecraft/Personal/Instances"))
                && evidence.startsWith(installation.resolve("evidence"))
                && !Files.isSymbolicLink(world.getParent())
                && Boolean.getBoolean("link.e4steam.launcherManagedLan"), "Wrong Java 8 QA fixture.");
        if (reopenLevelSha256.isEmpty()) {
            require(!Files.exists(world, LinkOption.NOFOLLOW_LINKS), "Fresh QA world already exists.");
        } else {
            verifyReopenLevel();
        }
    }

    public static void premain(String ignored, Instrumentation instrumentation) {
        try {
            Forge112SteamQaAgent probe = new Forge112SteamQaAgent();
            Thread worker = new Thread(() -> probe.run(instrumentation), "Forge-1.12-Steam-QA");
            worker.setDaemon(true);
            worker.start();
        } catch (Exception failure) {
            System.err.println("[Forge112SteamQa] Could not start the QA fixture: " + failure.getClass().getSimpleName());
        }
    }

    /** Called from the test-only Forge client tick hook, outside Minecraft's task-queue monitor. */
    public static void runClientTickTasks() {
        Runnable next;
        while ((next = CLIENT_TICK_TASKS.poll()) != null) next.run();
    }

    private void run(Instrumentation instrumentation) {
        try {
            event("started", "\"nativeRuntime\":true,\"transformer\":false,\"computerUse\":false");
            await(165, () -> findClient(instrumentation), "Minecraft client");
            await(180, () -> scheduled(client, this::titleReady), "title");
            event("title", "\"loaded\":true");
            scheduled(client, () -> { recordModSources(); return null; });
            boolean reopen = !reopenLevelSha256.isEmpty();
            scheduled(client, () -> { openWorld(reopen); return null; });
            await(115, () -> scheduled(client, () -> player() != null && server() != null), "world join");
            event(reopen ? "world_reopened" : "world_created", "\"commands\":false,\"mode\":\"SURVIVAL\"");
            scheduled(client, () -> { publishFromNativePause(); return null; });
            event("published", "\"oneNativeAction\":true,\"intermediateScreen\":false,\"reports\":true");
            checkPermissions();
            boolean chat = awaitChat();
            event("chat", "\"portAddressInviteClose\":" + chat);
            Object server = scheduled(client, this::server);
            scheduled(server, () -> {
                call(call(server, "func_184103_al", NONE), "func_72389_g", NONE);
                return call(server, "func_71267_a", new Class<?>[] {boolean.class}, false);
            });
            require(Files.isRegularFile(world.resolve("level.dat"), LinkOption.NOFOLLOW_LINKS), "Native save is absent.");
            event("complete", "\"worldSaved\":true,\"worldReopened\":"
                    + (reopen ? "true" : "\"NOT RUN\"") + ",\"steamHostChat\":" + chat
                    + ",\"realSteamGuest\":\"NOT RUN\"");
        } catch (Throwable failure) {
            try {
                event("failure", "\"type\":\"" + failure.getClass().getSimpleName()
                        + "\",\"step\":\"" + safeFailure(failure) + "\"");
            } catch (Exception ignored) { }
        } finally {
            if (client != null) try {
                scheduled(client, () -> call(client, "func_71400_g", NONE));
            } catch (Exception ignored) { }
        }
    }

    private boolean findClient(Instrumentation instrumentation) throws Exception {
        // Resolving even one reflected Minecraft method can load all of its
        // signature classes. Wait until the native game loop has started so
        // the probe cannot load a mod's targets during Forge bootstrap.
        boolean running = false;
        for (StackTraceElement[] frames : Thread.getAllStackTraces().values()) {
            for (StackTraceElement frame : frames) {
                if (MC.equals(frame.getClassName()) && "func_71411_J".equals(frame.getMethodName())) {
                    running = true;
                    break;
                }
            }
            if (running) break;
        }
        if (!running) return false;
        for (Class<?> type : instrumentation.getAllLoadedClasses()) {
            if (!type.getName().equals(MC)) continue;
            Object value = invoke(type, null, "func_71410_x", NONE);
            if (value == null) continue;
            client = value;
            loader = type.getClassLoader();
            return true;
        }
        return false;
    }

    private boolean titleReady() throws Exception {
        Path actual = ((File) field(client, "field_71412_D")).toPath().toRealPath();
        require(actual.equals(game), "Wrong native game directory.");
        Object screen = screen();
        return screen != null && (type(GUI + "GuiMainMenu").isInstance(screen)
                || screen.getClass().getName().startsWith("lumien.custommainmenu.gui.GuiCustom"))
                && player() == null && server() == null;
    }

    private void openWorld(boolean reopen) throws Exception {
        require(titleReady(), "Opening a QA world requires the title screen.");
        if (reopen) {
            verifyReopenLevel();
            loadWorld(null);
            return;
        }
        require(!Files.exists(world, LinkOption.NOFOLLOW_LINKS), "Fresh world already exists.");
        Class<?> settings = type("net.minecraft.world.WorldSettings");
        Class<?> mode = type("net.minecraft.world.GameType");
        Class<?> worldType = type("net.minecraft.world.WorldType");
        Object fresh = settings.getConstructor(long.class, mode, boolean.class, boolean.class, worldType)
                .newInstance(10991L, mode.getField("SURVIVAL").get(null), true, false,
                        worldType.getField("field_77137_b").get(null));
        loadWorld(fresh);
    }

    private void verifyReopenLevel() throws Exception {
        Path level = world.resolve("level.dat");
        require(Files.isRegularFile(level, LinkOption.NOFOLLOW_LINKS) && level.toRealPath().equals(level)
                && Files.size(level) <= 16 * 1024 * 1024, "Invalid QA level file.");
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(level));
        StringBuilder hex = new StringBuilder(64);
        for (byte value : digest) hex.append(String.format("%02x", value & 0xff));
        require(reopenLevelSha256.contentEquals(hex), "QA level SHA256 changed.");
    }

    private void recordModSources() throws Exception {
        Class<?> loaderClass = type("net.minecraftforge.fml.common.Loader");
        Object forge = invoke(loaderClass, null, "instance", NONE);
        Path mods = game.resolve("mods").toRealPath();
        TreeMap<String, String> nativeRows = new TreeMap<>();
        for (Object mod : (List<?>) call(forge, "getActiveModList", NONE)) {
            String id = String.valueOf(call(mod, "getModId", NONE));
            if (id.equals("lanminecraft_java8_qa")) continue;
            String name = String.valueOf(call(mod, "getName", NONE));
            String version = String.valueOf(call(mod, "getVersion", NONE));
            File file = (File) call(mod, "getSource", NONE);
            Path source = file != null && file.isFile() ? file.toPath().toRealPath() : null;
            String relative = source != null && source.startsWith(mods)
                    ? game.relativize(source).toString().replace('\\', '/') : null;
            String row = "{\"id\":" + json(id) + ",\"name\":" + json(name) + ",\"version\":" + json(version)
                    + ",\"source\":" + (relative == null ? "null" : json(relative)) + "}";
            require(nativeRows.put(id, row) == null, "Duplicate native mod ID.");
        }
        event("loaded_mod_sources", "\"count\":" + nativeRows.size()
                + ",\"mods\":[" + String.join(",", nativeRows.values()) + "]");
    }

    private void loadWorld(Object settings) throws Exception {
        call(client, "func_71371_a", new Class<?>[] {String.class, String.class,
                type("net.minecraft.world.WorldSettings")}, worldName, worldName, settings);
    }

    private void publishFromNativePause() throws Exception {
        Object pause = type(GUI + "GuiIngameMenu").getConstructor().newInstance();
        setScreen(pause);
        List<?> buttons = (List<?>) field(pause, "field_146292_n");
        Object lan = lanSlot(buttons);
        require(translated("menu.shareToLan").equals(field(lan, "field_146126_j"))
                && noWorldControls(buttons), "Unexpected native pause controls.");
        Class<?> eventClass = type("net.minecraftforge.client.event.GuiScreenEvent$ActionPerformedEvent$Pre");
        Object action = eventClass.getConstructor(type(GUI + "GuiScreen"), type(GUI + "GuiButton"), List.class)
                .newInstance(pause, lan, buttons);
        Object bus = type("net.minecraftforge.common.MinecraftForge").getField("EVENT_BUS").get(null);
        require(Boolean.TRUE.equals(call(bus, "post",
                new Class<?>[] {type("net.minecraftforge.fml.common.eventhandler.Event")}, action)),
                "Managed native pause action was not handled.");
        require(screen() == null && Boolean.TRUE.equals(call(server(), "func_71344_c", NONE)),
                "A single native action did not publish and close the menu.");
        Object after = type(GUI + "GuiIngameMenu").getConstructor().newInstance();
        setScreen(after);
        List<?> afterButtons = (List<?>) field(after, "field_146292_n");
        require(translated("text.e4steam_minecraft.launcherReports")
                .equals(field(lanSlot(afterButtons), "field_146126_j")) && noWorldControls(afterButtons),
                "The published world did not show Reports in the native pause slot.");
        setScreen(null);
    }

    private static Object lanSlot(List<?> buttons) throws Exception {
        for (Object button : buttons) if (Integer.valueOf(7).equals(field(button, "field_146127_k"))) return button;
        throw new IllegalStateException("Missing native LAN slot.");
    }

    private static boolean noWorldControls(List<?> buttons) throws Exception {
        for (Object button : buttons) {
            String text = String.valueOf(field(button, "field_146126_j")).toLowerCase(java.util.Locale.ROOT);
            if (text.contains("cheats") || text.contains("читы") || text.contains("world settings")
                    || text.contains("настройки мира")) return false;
        }
        return true;
    }

    private void checkPermissions() throws Exception {
        Object server = scheduled(client, this::server);
        Object host = scheduled(client, () -> call(player(), "func_146103_bH", NONE));
        require("LanMcPackQa".equals(call(host, "getName", NONE)), "Unexpected host fixture profile.");
        Class<?> profile = type("com.mojang.authlib.GameProfile");
        Object guest = profile.getConstructor(UUID.class, String.class).newInstance(
                UUID.fromString("ad587e6a-e53c-43bd-955b-de8d677ff0dd"), "LanMcQaGuest");
        scheduled(server, () -> {
            Object data = call(call(server, "func_71218_a", new Class<?>[] {int.class}, 0), "func_72912_H", NONE);
            Object players = call(server, "func_184103_al", NONE);
            Object liveHost = call(players, "func_152612_a", new Class<?>[] {String.class}, "LanMcPackQa");
            require(liveHost != null && !allowsGameMode(liveHost), "Live host could change game mode.");
            require(!allows(players, profile, host) && !allows(players, profile, guest), "Commands initially granted.");
            call(data, "func_176121_c", new Class<?>[] {boolean.class}, true);
            call(players, "func_72387_b", new Class<?>[] {boolean.class}, true);
            require(Boolean.FALSE.equals(call(data, "func_76086_u", NONE))
                    && "SURVIVAL".equals(((Enum<?>) call(data, "func_76077_q", NONE)).name())
                    && !allows(players, profile, host) && !allows(players, profile, guest)
                    && !allowsGameMode(liveHost),
                    "A mutation granted commands or changed the saved world policy.");
            return null;
        });
        event("permissions", "\"nativePlayerList\":true,\"hostDenied\":true,\"syntheticGuestDenied\":true,"
                + "\"liveHostGameModeDenied\":true,\"worldSetterDenied\":true,\"grantSetterDenied\":true");
    }

    private static boolean allows(Object players, Class<?> profile, Object player) throws Exception {
        return Boolean.TRUE.equals(call(players, "func_152596_g", new Class<?>[] {profile}, player));
    }

    private static boolean allowsGameMode(Object player) throws Exception {
        return Boolean.TRUE.equals(call(player, "func_70003_b", new Class<?>[] {int.class, String.class},
                2, "gamemode"));
    }

    private boolean awaitChat() throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(80);
        while (System.nanoTime() < deadline) {
            if (scheduled(client, this::completeChat)) return true;
            Thread.sleep(500);
        }
        return false;
    }

    private boolean completeChat() throws Exception {
        Object chat = call(field(client, "field_71456_v"), "func_146158_b", NONE);
        boolean port = false;
        boolean sharing = false;
        for (Object line : (List<?>) field(chat, "field_146252_h")) {
            Object message = call(line, "func_151461_a", NONE);
            String text = String.valueOf(call(message, "func_150260_c", NONE));
            String portPrefix = translated("text.e4steam_minecraft.launcherLocalPort", "").replace("[]", "");
            port |= text.startsWith(portPrefix);
            if (!text.startsWith(translated("text.e4steam_minecraft.launcherDomainAssigned"))) continue;
            java.util.Set<String> actions = new java.util.HashSet<>();
            for (Object component : (Iterable<?>) message) {
                Object action = call(call(component, "func_150256_b", NONE), "func_150235_h", NONE);
                if (action != null) actions.add(String.valueOf(call(action, "func_150668_b", NONE)));
            }
            sharing |= actions.contains("/e4steam copy") && actions.contains("/e4steam invite")
                    && actions.contains("/e4steam stop");
        }
        return port && sharing;
    }

    private String translated(String key, Object... arguments) throws Exception {
        return (String) invoke(type("net.minecraft.client.resources.I18n"), null, "func_135052_a",
                new Class<?>[] {String.class, Object[].class}, key, arguments);
    }

    private <T> T scheduled(Object target, Callable<T> operation) throws Exception {
        CompletableFuture<T> result = new CompletableFuture<>();
        Runnable task = () -> {
            try { result.complete(operation.call()); }
            catch (Throwable failure) { result.completeExceptionally(failure); }
        };
        if (target == client) CLIENT_TICK_TASKS.add(task);
        else call(target, "func_152344_a", new Class<?>[] {Runnable.class}, task);
        return result.get(120, TimeUnit.SECONDS);
    }

    private void await(int seconds, Callable<Boolean> condition, String phase) throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(seconds);
        do {
            if (condition.call()) return;
            Thread.sleep(250);
        } while (System.nanoTime() < deadline);
        throw new IllegalStateException("Timed out: " + phase);
    }

    private static String safeFailure(Throwable failure) {
        Throwable detail = failure;
        while (detail.getCause() != null) detail = detail.getCause();
        String text = detail instanceof IllegalStateException || detail instanceof NoSuchMethodException
                || detail instanceof NoSuchFieldException || detail instanceof LinkageError
                ? String.valueOf(detail.getMessage()) : "";
        return text.replaceAll("[^A-Za-z0-9 .:_()/-]", "?");
    }

    private static Path path(String key) throws Exception {
        return Paths.get(System.getProperty(PREFIX + key)).toRealPath();
    }

    private Class<?> type(String name) throws ClassNotFoundException {
        return Class.forName(name, false, loader);
    }

    private Object screen() throws Exception { return field(client, "field_71462_r"); }
    private Object player() throws Exception { return field(client, "field_71439_g"); }
    private Object server() throws Exception { return call(client, "func_71401_C", NONE); }
    private void setScreen(Object next) throws Exception {
        call(client, "func_147108_a", new Class<?>[] {type(GUI + "GuiScreen")}, next);
    }

    private static Object field(Object target, String name) throws Exception {
        for (Class<?> type = target.getClass(); type != null; type = type.getSuperclass()) {
            try {
                Field field = type.getDeclaredField(name);
                field.setAccessible(true);
                return field.get(target);
            } catch (NoSuchFieldException absent) { }
        }
        throw new NoSuchFieldException(name);
    }

    private static Object call(Object target, String name, Class<?>[] types, Object... args) throws Exception {
        return invoke(target.getClass(), target, name, types, args);
    }

    private static Object invoke(Class<?> owner, Object target, String name, Class<?>[] types, Object... args)
            throws Exception {
        for (Class<?> type = owner; type != null; type = type.getSuperclass()) {
            try {
                Method method = type.getDeclaredMethod(name, types);
                method.setAccessible(true);
                return method.invoke(target, args);
            } catch (NoSuchMethodException absent) { }
        }
        throw new NoSuchMethodException(owner.getName() + "." + name);
    }

    private void event(String kind, String fields) throws Exception {
        String record = "{\"kind\":\"" + kind + "\"," + fields + "}\n";
        Files.write(evidence, record.getBytes(StandardCharsets.UTF_8),
                StandardOpenOption.CREATE, StandardOpenOption.APPEND);
    }

    private static String json(String value) {
        StringBuilder text = new StringBuilder("\"");
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            if (c == '"' || c == '\\') text.append('\\').append(c);
            else if (c < 32) text.append(String.format("\\u%04x", (int) c));
            else text.append(c);
        }
        return text.append('"').toString();
    }

    private static void require(boolean condition, String error) {
        if (!condition) throw new IllegalStateException(error);
    }
}
