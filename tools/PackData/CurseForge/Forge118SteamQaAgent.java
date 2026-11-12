package lanminecraft.qa;

import java.io.File;
import java.io.IOException;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.security.MessageDigest;
import java.time.Instant;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.StringJoiner;
import java.util.TreeMap;
import java.util.UUID;
import java.util.concurrent.Callable;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.function.Function;

/** Test-only native assertions for reviewed Forge clients. No input automation. */
public final class Forge118SteamQaAgent {
    private static final String PREFIX = "lanminecraft.steamqa.";
    private static final String MC = "net.minecraft.client.Minecraft";
    private static final String SCREENS = "net.minecraft.client.gui.screens.";
    private static final String CHAT = "net.minecraft.network.chat.";
    private static final String LEVEL = "net.minecraft.world.level.";
    private static final String PLAYER_NAME = "LanMcPackQa";

    private final Path game, evidence, world;
    private final String minecraftVersion;
    private final String worldName;
    private final String reopenLevelSha256;
    private final long deadline = System.nanoTime() + TimeUnit.MINUTES.toNanos(11);
    private Object client;
    private ClassLoader gameLoader;

    private Forge118SteamQaAgent() throws Exception {
        Path root = path("run");
        Path parent = root.getParent();
        if (parent == null || parent.getParent() == null || !parent.getFileName().toString().equals("agent-runs") ||
                !parent.getParent().getFileName().toString().equals("artifacts") ||
                !Files.isRegularFile(root.resolve("RUN.json"), LinkOption.NOFOLLOW_LINKS)) {
            throw new SecurityException("Expected a marked agent run.");
        }
        Path install = root.resolve("runtime-qa");
        game = path("game");
        evidence = path("evidence");
        minecraftVersion = System.getProperty(PREFIX + "minecraftVersion", "1.18.2");
        String worldPrefix = switch (minecraftVersion) {
            case "1.18.2" -> "LanMcSteamQa118";
            case "1.19.2" -> "LanMcSteamQa119";
            case "1.20.1" -> "LanMcSteamQa120";
            default -> throw new SecurityException("No reviewed Forge QA profile for " + minecraftVersion);
        };
        String suffix = System.getProperty(PREFIX + "worldSuffix", "");
        if (!suffix.matches("[A-Za-z0-9_-]{0,24}")) throw new SecurityException("Invalid test-world suffix.");
        worldName = worldPrefix + suffix;
        world = game.resolve("saves").resolve(worldName);
        reopenLevelSha256 = System.getProperty(PREFIX + "reopenLevelSha256", "");
        if (!game.startsWith(install.resolve("Minecraft/Personal/Instances")) ||
                !evidence.startsWith(install.resolve("evidence")) ||
                Files.exists(evidence.resolve("native-steam-qa.jsonl"), LinkOption.NOFOLLOW_LINKS) ||
                Files.isSymbolicLink(world.getParent())) {
            throw new SecurityException("Expected an isolated game and fresh evidence directory.");
        }
        verifyWorldInput();
        if (!Boolean.getBoolean("minecraft.portable.identity.lanPublishEnabled") ||
                !Boolean.getBoolean("minecraft.portable.identity.lanPreserveWorldSettingsEnabled") ||
                legacyComponents() && !Boolean.getBoolean("minecraft.portable.identity.pauseLegacyReportingEnabled")) {
            throw new SecurityException("The exact production world sharing hooks are required.");
        }
    }

    public static void premain(String ignored, Instrumentation instrumentation) {
        try {
            var agent = new Forge118SteamQaAgent();
            Thread worker = new Thread(() -> agent.run(instrumentation), "Forge-Steam-QA-" + agent.minecraftVersion);
            worker.setDaemon(true);
            worker.start();
        } catch (Exception failure) {
            System.err.println("[Forge118SteamQa] Agent refused: " + failure.getClass().getName() + ": " + failure.getMessage());
        }
    }

    private void run(Instrumentation instrumentation) {
        try {
            event("started", Map.of("world", worldName, "minecraft", minecraftVersion, "computerUse", false,
                "transforms", false, "twoAccounts", "NOT RUN"));
            waitForClient(instrumentation);
            waitUntil("title", 180, () -> onClient(() -> titleReady()));
            event("loaded_mod_ids", onClient(this::readMods));
            onClient(() -> { openWorld(); return null; });
            waitUntil("world join", 180, () -> onClient(() -> player() != null && server() != null));
            event(reopenLevelSha256.isEmpty() ? "world_created" : "world_reopened",
                Map.of("gameMode", "SURVIVAL", "commands", false));

            onClient(() -> { beginPublish(); return null; });
            waitUntil("world published and settings gone", 20, () -> onClient(() ->
                Boolean.TRUE.equals(invoke(server(), "m_6992_")) && screen() == null));
            event("published", Map.of("nativeListener", true, "settingsClosedOnPublication", true));

            checkPermissions();
            event("pause_after_publish", onClient(this::checkPauseAfter));
            ChatProbe steamChat = waitForChat();
            event("chat", steamChat.evidence());

            saveWorld();
            event("complete", Map.of("nativeSteamHost", steamChat.complete() ? "PASS" : "FAIL", "guestConnection", "NOT RUN"));
        } catch (Exception failure) {
            Throwable detail = failure;
            while (detail.getCause() != null) detail = detail.getCause();
            try { event("failure", Map.of("exception", detail.getClass().getName(), "message", String.valueOf(detail.getMessage()))); }
            catch (IOException ignored) { }
        } finally {
            if (client != null) try {
                ((Executor) client).execute(() -> {
                    try { invoke(client, "m_91395_"); }
                    catch (ReflectiveOperationException failure) {
                        System.err.println("[Forge118SteamQa] Native shutdown failed: " + failure.getClass().getName());
                    }
                });
            } catch (RuntimeException ignored) { }
        }
    }

    private void waitForClient(Instrumentation instrumentation) throws Exception {
        waitUntil("Minecraft instance", 180, () -> {
            // The exact native run loop establishes that the singleton was
            // initialized by Minecraft itself before the agent reflects on it.
            if (!Thread.getAllStackTraces().values().stream().flatMap(java.util.Arrays::stream)
                    .anyMatch(frame -> frame.getClassName().equals(MC) && frame.getMethodName().equals("m_91374_"))) return false;
            for (Class<?> loaded : instrumentation.getAllLoadedClasses()) {
                if (!loaded.getName().equals(MC)) continue;
                client = invoke(loaded, null, "m_91087_", new Class<?>[0]);
                if (client == null) continue;
                gameLoader = loaded.getClassLoader();
                return true;
            }
            return false;
        });
    }

    private boolean titleReady() throws Exception {
        require(((File) field(client.getClass(), "f_91069_").get(client)).toPath().toRealPath().equals(game),
            "Wrong actual game directory.");
        return type(SCREENS + "TitleScreen").isInstance(screen()) &&
            invoke(client, "m_91265_") == null && player() == null && server() == null;
    }

    private Map<String, Object> readMods() throws Exception {
        Class<?> modList = type("net.minecraftforge.fml.ModList");
        Object list = invoke(modList, null, "get", new Class<?>[0]);
        Map<String, String> ids = new TreeMap<>();
        Map<String, String> files = new TreeMap<>();
        for (Object info : (Iterable<?>) invoke(list, "getMods")) {
            String id = String.valueOf(invoke(info, "getModId"));
            String version = String.valueOf(invoke(info, "getVersion"));
            require(ids.putIfAbsent(id, version) == null, "Duplicate loaded mod ID.");
            Object fileInfo = invoke(info, "getOwningFile");
            Object file = fileInfo == null ? null : invoke(fileInfo, "getFile");
            Path jar = file == null ? null : (Path) invoke(file, "getFilePath");
            if (jar != null && jar.getFileName() != null && jar.getFileName().toString().endsWith(".jar")) {
                files.put(id, jar.getFileName().toString());
            }
        }
        require(minecraftVersion.equals(ids.get("minecraft")), "Unexpected runtime Minecraft version.");
        require(ids.containsKey("e4steam") || ids.containsKey("e4steam_minecraft"), "Steam mod is absent.");
        return Map.of("count", ids.size(), "ids", ids, "sourceFiles", files);
    }

    private void openWorld() throws Exception {
        require(titleReady(), "World open requires the native title screen.");
        verifyWorldInput();
        Object worldFlows = legacyComponents() ? client : invoke(client, "m_231466_");
        if (!reopenLevelSha256.isEmpty()) {
            if (legacyComponents()) {
                invoke(client, "m_91200_", new Class<?>[] { String.class }, worldName);
            } else {
                invoke(worldFlows, "m_233133_", new Class<?>[] { type(SCREENS + "Screen"), String.class }, screen(), worldName);
            }
            return;
        }
        Class<?> settings = type(LEVEL + "LevelSettings");
        Class<?> mode = type(LEVEL + "GameType");
        Class<?> difficulty = type("net.minecraft.world.Difficulty");
        Class<?> gameRules = type(LEVEL + "GameRules");
        boolean worldDimensions = minecraftVersion.equals("1.20.1");
        Class<?> dataPacks = type(LEVEL + (worldDimensions ? "WorldDataConfiguration" : "DataPackConfig"));
        Class<?> registry = type("net.minecraft.core.RegistryAccess");
        Object levelSettings = settings.getConstructor(String.class, mode, boolean.class, difficulty, boolean.class,
            gameRules, dataPacks).newInstance(worldName, mode.getField("SURVIVAL").get(null), false,
                difficulty.getField("NORMAL").get(null), false, gameRules.getConstructor().newInstance(),
                dataPacks.getField(worldDimensions ? "f_244649_" : "f_45842_").get(null));
        if (worldDimensions) {
            Class<?> options = type(LEVEL + "levelgen.WorldOptions");
            Class<?> presets = type(LEVEL + "levelgen.presets.WorldPresets");
            Object generationOptions = invoke(options, null, "m_247394_", new Class<?>[0]);
            Function<Object, Object> dimensions = access -> {
                Thread thread = Thread.currentThread();
                ClassLoader previous = thread.getContextClassLoader();
                thread.setContextClassLoader(gameLoader);
                try { return invoke(presets, null, "m_246552_", new Class<?>[] { registry }, access); }
                catch (ReflectiveOperationException failure) { throw new IllegalStateException(failure); }
                finally { thread.setContextClassLoader(previous); }
            };
            invoke(worldFlows, "m_233157_", new Class<?>[] { String.class, settings, options, Function.class },
                worldName, levelSettings, generationOptions, dimensions);
            return;
        }
        Class<?> generation = type(LEVEL + "levelgen.WorldGenSettings");
        Object access = invoke(registry, null, "m_206197_", new Class<?>[0]);
        Object worldGen = legacyComponents()
            ? invoke(generation, null, "m_190050_", new Class<?>[] { registry }, access)
            : invoke(type(LEVEL + "levelgen.presets.WorldPresets"), null, "m_226449_", new Class<?>[] { registry }, access);
        invoke(worldFlows, legacyComponents() ? "m_205185_" : "m_233157_",
            new Class<?>[] { String.class, settings, registry, generation }, worldName, levelSettings, access, worldGen);
    }

    private void verifyWorldInput() throws Exception {
        if (reopenLevelSha256.isEmpty()) {
            require(!Files.exists(world, LinkOption.NOFOLLOW_LINKS), "Fresh QA world already exists.");
            return;
        }
        require(reopenLevelSha256.matches("[a-f0-9]{64}") && Files.isDirectory(world, LinkOption.NOFOLLOW_LINKS),
            "Reopening requires a reserved QA world and a pinned level.dat.");
        require(reopenLevelSha256.equals(levelDatSha256()), "The pinned QA world changed.");
    }

    private String levelDatSha256() throws Exception {
        Path data = world.resolve("level.dat");
        require(Files.isRegularFile(data, LinkOption.NOFOLLOW_LINKS) && Files.size(data) < 16 * 1024 * 1024,
            "Expected an ordinary QA level.dat.");
        return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(data)));
    }

    private void saveWorld() throws Exception {
        Object server = onClient(this::server);
        require(Boolean.TRUE.equals(schedule((Executor) server, () -> invoke(server, "m_195514_",
            new Class<?>[] { boolean.class, boolean.class, boolean.class }, false, true, false))),
            "Native saveEverything did not save the QA world.");
        event("world_saved", Map.of("levelDatSha256", levelDatSha256(), "levelDatBytes", Files.size(world.resolve("level.dat"))));
        // Shutdown saves normally after the run loop. An in-process disconnect
        // renders a forced progress frame and can block on hidden GL windows.
    }

    private void beginPublish() throws Exception {
        Object pause = type(SCREENS + "PauseScreen").getConstructor(boolean.class).newInstance(true);
        setScreen(pause);
        List<Object> actions = visibleActions(pause, "menu.shareToLan");
        require(actions.size() == 1, "Native pause menu did not have one Share to LAN action.");
        require(noWorldControls(pause), "The original pause menu exposed command or world settings controls.");
        Object action = actions.get(0);
        require(type("net.minecraft.client.gui.components.AbstractButton").isInstance(action) && isActive(action),
            "Share to LAN was not an active native button.");
        event("pause_before_publish", Map.of("shareToLan", 1, "worldSettingsOrCheats", false, "nativeButtonCallback", true));
        invoke(action, "m_5691_");
    }

    private Map<String, Boolean> checkPauseAfter() throws Exception {
        Object pause = type(SCREENS + "PauseScreen").getConstructor(boolean.class).newInstance(true);
        setScreen(pause);
        List<Object> reportActions = visibleActions(pause, "menu.playerReporting", "Жалобы", "Player Reporting");
        boolean reports = reportActions.size() == 1 && isActive(reportActions.get(0));
        boolean shareGone = visibleActions(pause, "menu.shareToLan").isEmpty();
        boolean noWorldControls = noWorldControls(pause);
        setScreen(null);
        require(reports && shareGone && noWorldControls, "Published pause menu did not replace Share to LAN with Reports.");
        return Map.of("reports", reports, "shareToLanGone", shareGone, "worldSettingsOrCheats", !noWorldControls);
    }

    private void checkPermissions() throws Exception {
        Object server = onClient(this::server);
        Object host = onClient(() -> invoke(player(), "m_36316_"));
        Class<?> profile = type("com.mojang.authlib.GameProfile");
        require(PLAYER_NAME.equals(invoke(host, "getName")), "Wrong native QA player.");
        Object guest = profile.getConstructor(UUID.class, String.class).newInstance(
            UUID.fromString("d96a4354-8ff0-4b76-8202-6835ef952efb"), "LanMcQaGuest");
        Map<String, Boolean> result = schedule((Executor) server, () -> {
            Object players = invoke(server, "m_6846_");
            Object data = invoke(server, "m_129910_");
            boolean hostBefore = Boolean.TRUE.equals(invoke(players, "m_11303_", new Class<?>[] { profile }, host));
            boolean guestBefore = Boolean.TRUE.equals(invoke(players, "m_11303_", new Class<?>[] { profile }, guest));
            invoke(players, "m_11284_", new Class<?>[] { boolean.class }, true);
            boolean hostAfter = Boolean.TRUE.equals(invoke(players, "m_11303_", new Class<?>[] { profile }, host));
            boolean guestAfter = Boolean.TRUE.equals(invoke(players, "m_11303_", new Class<?>[] { profile }, guest));
            boolean stored = Boolean.TRUE.equals(invoke(data, "m_5468_"));
            boolean survival = "SURVIVAL".equals(((Enum<?>) invoke(data, "m_5464_")).name());
            return Map.of("hostBefore", hostBefore, "guestBefore", guestBefore, "hostAfter", hostAfter,
                "guestAfter", guestAfter, "savedAllowsCommands", stored, "savedSurvivalMode", survival);
        });
        event("native_permissions", Map.of("actualPlayerList", true, "syntheticGuest", true, "checks", result));
        require(result.get("savedSurvivalMode") && result.entrySet().stream()
            .noneMatch(check -> !check.getKey().equals("savedSurvivalMode") && check.getValue()),
            "Native PlayerList granted command permission in a commands-off world.");
    }

    private ChatProbe waitForChat() throws Exception {
        long until = Math.min(deadline, System.nanoTime() + TimeUnit.SECONDS.toNanos(75));
        ChatProbe probe;
        do {
            probe = onClient(this::readChat);
            if (probe.complete()) return probe;
            Thread.sleep(1000);
        } while (System.nanoTime() < until);
        return probe;
    }

    private ChatProbe readChat() throws Exception {
        Object gui = field(client.getClass(), "f_91065_").get(client);
        // Read the native local chat. Mods may redirect getChat() to a
        // different selected channel and forward system messages to this HUD.
        Object chat = field(gui.getClass(), "f_92988_").get(gui);
        var probe = new ChatProbe();
        for (Object entry : (List<?>) field(chat.getClass(), "f_93760_").get(chat)) {
            Object message = invoke(entry, legacyComponents() ? "m_90793_" : "f_240363_");
            if ("commands.publish.started".equals(translationKey(message))) probe.portLines++;
            if (!"text.e4steam_minecraft.domainAssigned".equals(translationKey(message))) continue;
            probe.readyLines++;
            inspectActions(message, probe, 0);
        }
        return probe;
    }

    private void inspectActions(Object component, ChatProbe probe, int depth) throws Exception {
        require(depth < 20, "Chat component is too deeply nested.");
        Object style = invoke(component, "m_7383_");
        Object click = invoke(style, "m_131182_");
        if (click != null) {
            String action = ((Enum<?>) invoke(click, "m_130622_")).name();
            String value = (String) invoke(click, "m_130623_");
            Object color = invoke(style, "m_131135_");
            int rgb = color == null ? -1 : (Integer) invoke(color, "m_131265_");
            if ("COPY_TO_CLIPBOARD".equals(action) && value.endsWith(".steam") && rgb == 0x55ff55) probe.address = true;
            if ("RUN_COMMAND".equals(action) && "/e4steam invite".equals(value) && rgb == 0x5555ff) probe.invite = true;
            if ("RUN_COMMAND".equals(action) && "/e4steam stop".equals(value) && rgb == 0xff5555) probe.stop = true;
        }
        Object contents = translatableContents(component);
        if (contents != null) {
            for (Object arg : (Object[]) invoke(contents, legacyComponents() ? "m_131329_" : "m_237523_")) {
                if (type(CHAT + "Component").isInstance(arg)) inspectActions(arg, probe, depth + 1);
            }
        }
        for (Object sibling : (List<?>) invoke(component, "m_7360_")) inspectActions(sibling, probe, depth + 1);
    }

    private static final class ChatProbe {
        int portLines, readyLines;
        boolean address, invite, stop;
        boolean complete() { return portLines == 1 && readyLines == 1 && address && invite && stop; }
        Map<String, Object> evidence() {
            return Map.of("localPortLines", portLines, "readyLines", readyLines, "greenCopyableAddress", address,
                "blueInviteAction", invite, "redCloseAction", stop, "privateEndpointRecorded", false, "nativeLocalHistory", true);
        }
    }

    private boolean noWorldControls(Object pause) throws Exception {
        for (Object widget : (List<?>) invoke(pause, "m_6702_")) {
            if (!type("net.minecraft.client.gui.components.AbstractWidget").isInstance(widget)) continue;
            Object label = invoke(widget, "m_6035_");
            String key = translationKey(label);
            String text = (String) invoke(label, "getString");
            if (List.of("selectWorld.allowCommands", "lanServer.allowCommands", "menu.worldOptions", "menu.worldSettings").contains(key == null ? "" : key) ||
                    List.of("World Settings", "Настройки мира", "Enable Cheats", "Включить читы").contains(text)) return false;
        }
        return true;
    }

    private List<Object> visibleActions(Object pause, String key, String... labels) throws Exception {
        List<Object> actions = new ArrayList<>();
        List<String> translations = List.of(labels);
        for (Object widget : (List<?>) invoke(pause, "m_6702_")) {
            if (!type("net.minecraft.client.gui.components.AbstractWidget").isInstance(widget)) continue;
            if (!Boolean.TRUE.equals(field(widget.getClass(), "f_93624_").get(widget))) continue;
            Object label = invoke(widget, "m_6035_");
            if (key.equals(translationKey(label)) || translations.contains(invoke(label, "getString"))) actions.add(widget);
        }
        return actions;
    }

    private static boolean isActive(Object widget) throws ReflectiveOperationException {
        return Boolean.TRUE.equals(field(widget.getClass(), "f_93623_").get(widget));
    }

    private String translationKey(Object component) throws Exception {
        Object contents = translatableContents(component);
        return contents == null ? null : (String) invoke(contents, legacyComponents() ? "m_131328_" : "m_237508_");
    }

    private Object translatableContents(Object component) throws Exception {
        if (legacyComponents()) return type(CHAT + "TranslatableComponent").isInstance(component) ? component : null;
        Object contents = invoke(component, "m_214077_");
        return type(CHAT + "contents.TranslatableContents").isInstance(contents) ? contents : null;
    }

    private boolean legacyComponents() { return minecraftVersion.equals("1.18.2"); }

    private void setScreen(Object screen) throws ReflectiveOperationException {
        invoke(client, "m_91152_", new Class<?>[] { type(SCREENS + "Screen") }, screen);
    }
    private Object screen() throws ReflectiveOperationException { return field(client.getClass(), "f_91080_").get(client); }
    private Object player() throws ReflectiveOperationException { return field(client.getClass(), "f_91074_").get(client); }
    private Object server() throws ReflectiveOperationException { return invoke(client, "m_91092_"); }
    private Class<?> type(String name) throws ClassNotFoundException { return Class.forName(name, false, gameLoader); }

    private <T> T onClient(Callable<T> action) throws Exception { return schedule((Executor) client, action); }
    private <T> T schedule(Executor executor, Callable<T> action) throws Exception {
        CompletableFuture<T> result = new CompletableFuture<>();
        executor.execute(() -> {
            try { result.complete(action.call()); }
            catch (Throwable failure) { result.completeExceptionally(failure); }
        });
        long remaining = deadline - System.nanoTime();
        if (remaining <= 0) throw new TimeoutException("Native QA deadline reached.");
        return result.get(remaining, TimeUnit.NANOSECONDS);
    }

    private void waitUntil(String name, int seconds, Callable<Boolean> condition) throws Exception {
        long until = Math.min(deadline, System.nanoTime() + TimeUnit.SECONDS.toNanos(seconds));
        do {
            if (condition.call()) return;
            Thread.sleep(250);
        } while (System.nanoTime() < until);
        throw new TimeoutException("Timed out waiting for " + name + ".");
    }

    private static Object invoke(Object target, String name) throws ReflectiveOperationException {
        return invoke(target, name, new Class<?>[0]);
    }
    private static Object invoke(Object target, String name, Class<?>[] types, Object... args) throws ReflectiveOperationException {
        return invoke(target.getClass(), target, name, types, args);
    }
    private static Object invoke(Class<?> owner, Object target, String name, Class<?>[] types, Object... args)
            throws ReflectiveOperationException {
        Method method;
        try { method = owner.getMethod(name, types); }
        catch (NoSuchMethodException failure) {
            method = null;
            for (Class<?> current = owner; current != null; current = current.getSuperclass()) {
                try { method = current.getDeclaredMethod(name, types); break; }
                catch (NoSuchMethodException ignored) { }
            }
            if (method == null) throw failure;
        }
        method.setAccessible(true);
        return method.invoke(target, args);
    }

    private static Field field(Class<?> owner, String name) throws NoSuchFieldException {
        for (Class<?> current = owner; current != null; current = current.getSuperclass()) {
            try {
                Field field = current.getDeclaredField(name);
                field.setAccessible(true);
                return field;
            } catch (NoSuchFieldException ignored) { }
        }
        throw new NoSuchFieldException(owner.getName() + "." + name);
    }

    private static Path path(String name) throws IOException {
        String value = System.getProperty(PREFIX + name);
        if (value == null || value.isBlank()) throw new IllegalArgumentException("Missing " + PREFIX + name);
        Path path = Path.of(value).toAbsolutePath().normalize();
        Path real = path.toRealPath();
        if (!path.equals(real) || !Files.isDirectory(path, LinkOption.NOFOLLOW_LINKS)) {
            throw new SecurityException("Expected an ordinary QA directory for " + name);
        }
        return real;
    }

    private static void require(boolean condition, String message) {
        if (!condition) throw new IllegalStateException(message);
    }

    private void event(String kind, Map<String, ?> details) throws IOException {
        Map<String, Object> row = new LinkedHashMap<>();
        row.put("kind", kind);
        row.put("utc", Instant.now().toString());
        row.putAll(details);
        Files.writeString(evidence.resolve("native-steam-qa.jsonl"), json(row) + "\n",
            StandardOpenOption.CREATE, StandardOpenOption.APPEND);
        System.out.println("[Forge118SteamQa] " + kind);
    }

    private static String json(Object value) {
        if (value instanceof Number || value instanceof Boolean) return value.toString();
        if (value instanceof Map<?, ?> map) {
            var entries = new StringJoiner(",", "{", "}");
            map.forEach((key, entry) -> entries.add(json(key.toString()) + ":" + json(entry)));
            return entries.toString();
        }
        var text = new StringBuilder("\"");
        for (char character : String.valueOf(value).toCharArray()) {
            if (character == '\\' || character == '"') text.append('\\').append(character);
            else if (character < 32) text.append(String.format("\\u%04x", (int) character));
            else text.append(character);
        }
        return text.append('"').toString();
    }
}
