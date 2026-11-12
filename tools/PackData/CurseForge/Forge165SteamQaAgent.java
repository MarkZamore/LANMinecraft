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
import java.util.Map;
import java.util.TreeMap;
import java.util.UUID;
import java.util.concurrent.Callable;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executor;
import java.util.concurrent.TimeUnit;

/** Exact Forge 36.2.34 lifecycle witness. No transformer, external input or content changes. */
public final class Forge165SteamQaAgent {
    private static final String PREFIX = "lanminecraft.steamqa.";
    private static final String MC = "net.minecraft.client.Minecraft";
    private static final String SCREEN = "net.minecraft.client.gui.screen.";
    private static final String TRANSLATION = "net.minecraft.util.text.TranslationTextComponent";
    private static final String PORT = "text.e4steam_lanmc.localPort";
    private static final String READY = "text.e4steam_minecraft.domainAssigned";
    private static final String REPORTS = "text.e4steam_lanmc.reports";
    private static final Class<?>[] NONE = new Class<?>[0];
    private final Path game;
    private final Path evidence;
    private final Path world;
    private final String worldName;
    private final String reopenLevelSha256;
    private final long deadline = System.nanoTime() + TimeUnit.MINUTES.toNanos(11);
    private Object client;
    private ClassLoader loader;

    private Forge165SteamQaAgent() throws Exception {
        Path run = path("run");
        Path installation = run.resolve("runtime-qa");
        game = path("game");
        evidence = path("evidence").resolve("native-steam-qa-165.jsonl");
        String suffix = System.getProperty(PREFIX + "worldSuffix", "");
        require(suffix.matches("[A-Za-z0-9_-]{0,16}"), "Invalid QA world suffix.");
        worldName = "LanMcSteamQa165" + suffix;
        world = game.resolve("saves").resolve(worldName);
        reopenLevelSha256 = System.getProperty(PREFIX + "reopenLevelSha256", "");
        require(run.getParent().getFileName().toString().equals("agent-runs")
                && run.getParent().getParent().getFileName().toString().equals("artifacts")
                && Files.isRegularFile(run.resolve("RUN.json"), LinkOption.NOFOLLOW_LINKS)
                && game.startsWith(installation.resolve("Minecraft/Personal/Instances"))
                && evidence.startsWith(installation.resolve("evidence"))
                && !Files.exists(evidence, LinkOption.NOFOLLOW_LINKS)
                && !Files.isSymbolicLink(world.getParent())
                && Boolean.getBoolean("link.e4steam.launcherManagedLan"), "Wrong Forge 1.16.5 QA fixture.");
        verifyWorldInput();
    }

    public static void premain(String ignored, Instrumentation instrumentation) {
        try {
            Forge165SteamQaAgent agent = new Forge165SteamQaAgent();
            Thread worker = new Thread(() -> agent.run(instrumentation), "Forge-1.16.5-Steam-QA");
            worker.setDaemon(true);
            worker.start();
        } catch (Exception failure) {
            System.err.println("[Forge165SteamQa] Could not initialize: " + failure.getClass().getSimpleName());
        }
    }

    private void run(Instrumentation instrumentation) {
        String phase = "startup";
        try {
            event("started", "\"nativeRuntime\":true,\"computerUse\":false,\"transformer\":false");
            await("native game loop", 220, () -> findClient(instrumentation));
            await("title", 200, () -> schedule(client, this::titleReady));
            event("title", "\"loaded\":true");
            event("loaded_mod_ids", schedule(client, this::readMods));
            phase = reopenLevelSha256.isEmpty() ? "world creation" : "world reopen";
            schedule(client, () -> { openWorld(); return null; });
            await("native world", 125, () -> schedule(client, () -> player() != null && server() != null));
            checkPermissions(false);
            require(schedule(client, this::usesAuthorSkyblockGenerator), "The author default skyblock generator was not selected.");
            if (!reopenLevelSha256.isEmpty()) {
                event("world_reopened", "\"authorDefaultWorldType\":true,\"commands\":false,\"mode\":\"SURVIVAL\","
                        + "\"verifiedInputLevelDatSha256\":\"" + reopenLevelSha256 + "\"");
                event("complete", "\"worldReopened\":true,\"realSteamGuest\":\"NOT RUN\"");
                return;
            }
            event("world_created", "\"authorDefaultWorldType\":true,\"commands\":false,\"mode\":\"SURVIVAL\"");
            phase = "pause and publication";
            schedule(client, () -> { publishFromPause(); return null; });
            event("published", "\"oneNativeButtonAction\":true,\"intermediateScreen\":false,\"reports\":true");
            phase = "native command authority";
            checkPermissions(true);
            phase = "native chat";
            ChatProbe chat = awaitChat();
            event("chat", chat.evidence());
            phase = "native save";
            saveWorld();
            event("complete", "\"worldSaved\":true,\"nativeSteamHostChat\":" + chat.complete()
                    + ",\"realSteamGuest\":\"NOT RUN\"");
        } catch (Throwable failure) {
            Throwable detail = failure;
            while (detail.getCause() != null) detail = detail.getCause();
            String message = detail instanceof IllegalStateException || detail instanceof ReflectiveOperationException
                    ? String.valueOf(detail.getMessage()).replaceAll("[^A-Za-z0-9 .:_()/-]", "?") : "";
            try {
                event("failure", "\"phase\":\"" + phase + "\",\"type\":\"" + detail.getClass().getSimpleName()
                        + "\",\"message\":\"" + message + "\"");
            } catch (Exception ignored) { }
        } finally {
            if (client != null) ((Executor) client).execute(() -> {
                try { call(client, "func_71400_g", NONE); }
                catch (Exception failure) {
                    System.err.println("[Forge165SteamQa] Native shutdown failed: " + failure.getClass().getSimpleName());
                }
            });
        }
    }

    private boolean findClient(Instrumentation instrumentation) throws Exception {
        boolean running = Thread.getAllStackTraces().values().stream().flatMap(java.util.Arrays::stream)
                .anyMatch(frame -> MC.equals(frame.getClassName()) && "func_99999_d".equals(frame.getMethodName()));
        if (!running) return false;
        for (Class<?> type : instrumentation.getAllLoadedClasses()) {
            if (!type.getName().equals(MC)) continue;
            Object candidate = invoke(type, null, "func_71410_x", NONE);
            if (candidate == null) continue;
            client = candidate;
            loader = type.getClassLoader();
            return true;
        }
        return false;
    }

    private boolean titleReady() throws Exception {
        require(((File) field(client, "field_71412_D")).toPath().toRealPath().equals(game),
                "Wrong actual game directory.");
        return type(SCREEN + "MainMenuScreen").isInstance(screen())
                && call(client, "func_213250_au", NONE) == null && player() == null && server() == null;
    }

    private String readMods() throws Exception {
        Object list = invoke(type("net.minecraftforge.fml.ModList"), null, "get", NONE);
        Map<String, String> ids = new TreeMap<>();
        Map<String, String> files = new TreeMap<>();
        for (Object mod : (Iterable<?>) call(list, "getMods", NONE)) {
            String id = String.valueOf(call(mod, "getModId", NONE));
            String version = String.valueOf(call(mod, "getVersion", NONE));
            require(!id.isEmpty() && ids.put(id, version) == null, "Duplicate or empty native mod ID.");
            Object owner = call(mod, "getOwningFile", NONE);
            Object modFile = owner == null ? null : call(owner, "getFile", NONE);
            Path jar = modFile == null ? null : (Path) call(modFile, "getFilePath", NONE);
            if (jar != null && jar.isAbsolute() && jar.startsWith(game.resolve("mods"))) {
                Path real = jar.toRealPath();
                require(real.startsWith(game.resolve("mods")), "Native mod source left the QA mods directory.");
                files.put(id, game.relativize(real).toString().replace(File.separatorChar, '/'));
            }
        }
        require("1.16.5".equals(ids.get("minecraft")) && "36.2.34".equals(ids.get("forge"))
                && ids.containsKey("e4steam") && ids.containsKey("oneblock"), "Unexpected native client mod list.");
        return "\"count\":" + ids.size() + ",\"ids\":" + jsonMap(ids) + ",\"sourceFiles\":" + jsonMap(files);
    }

    private static String jsonMap(Map<String, String> values) {
        StringBuilder record = new StringBuilder("{");
        for (Map.Entry<String, String> entry : values.entrySet()) {
            if (record.charAt(record.length() - 1) != '{') record.append(',');
            record.append(jsonString(entry.getKey())).append(':').append(jsonString(entry.getValue()));
        }
        return record.append('}').toString();
    }

    private void openWorld() throws Exception {
        require(titleReady(), "World open requires native title.");
        verifyWorldInput();
        if (!reopenLevelSha256.isEmpty()) {
            call(client, "func_238191_a_", new Class<?>[] {String.class}, worldName);
            return;
        }
        Object creation = invoke(type(SCREEN + "CreateWorldScreen"), null, "func_243425_a",
                new Class<?>[] {type(SCREEN + "Screen")}, screen());
        setScreen(creation);
        Object name = field(creation, "field_146333_g");
        call(name, "func_146180_a", new Class<?>[] {String.class}, worldName);
        require(Boolean.FALSE.equals(field(creation, "field_146340_t"))
                && "SURVIVAL".equals(((Enum<?>) field(creation, "field_228197_f_")).name()),
                "Author creation screen did not default to survival and commands off.");
        call(field(creation, "field_195355_B"), "func_230930_b_", NONE);
    }

    private void verifyWorldInput() throws Exception {
        if (reopenLevelSha256.isEmpty()) {
            require(!Files.exists(world, LinkOption.NOFOLLOW_LINKS), "Fresh world already exists.");
            return;
        }
        require(reopenLevelSha256.matches("[a-f0-9]{64}") && Files.isDirectory(world, LinkOption.NOFOLLOW_LINKS)
                && reopenLevelSha256.equals(levelDatSha256()), "The pinned QA world changed.");
    }

    private String levelDatSha256() throws Exception {
        Path data = world.resolve("level.dat");
        require(Files.isRegularFile(data, LinkOption.NOFOLLOW_LINKS) && Files.size(data) < 16 * 1024 * 1024,
                "Expected an ordinary QA level.dat.");
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(data));
        StringBuilder hex = new StringBuilder(64);
        for (byte b : digest) hex.append(String.format("%02x", b & 0xff));
        return hex.toString();
    }

    private void saveWorld() throws Exception {
        Object server = schedule(client, this::server);
        schedule(server, () -> {
            call(call(server, "func_184103_al", NONE), "func_72389_g", NONE);
            require(Boolean.TRUE.equals(call(server, "func_213211_a",
                    new Class<?>[] {boolean.class, boolean.class, boolean.class}, false, true, false)),
                    "Native save did not save the QA world.");
            return null;
        });
        event("world_saved", "\"levelDatSha256\":\"" + levelDatSha256() + "\",\"levelDatBytes\":"
                + Files.size(world.resolve("level.dat")));
    }

    private boolean usesAuthorSkyblockGenerator() throws Exception {
        Object data = call(server(), "func_240793_aU_", NONE);
        Object settings = call(data, "func_230418_z_", NONE);
        Object generator = call(settings, "func_236225_f_", NONE);
        return generator.getClass().getName().equals(
                "de.melanx.skyblockbuilder.world.dimensions.overworld.SkyblockOverworldChunkGenerator");
    }

    private void publishFromPause() throws Exception {
        Object pause = type(SCREEN + "IngameMenuScreen").getConstructor(boolean.class).newInstance(true);
        setScreen(pause);
        require(button(pause, "menu.shareToLan") != null && button(pause, REPORTS) == null, "Missing native share action.");
        Object options = button(pause, "menu.options");
        require(options != null, "The ordinary options button is missing.");
        call(options, "func_230930_b_", NONE);
        Object optionsScreen = screen();
        require(type(SCREEN + "OptionsScreen").isInstance(optionsScreen), "The ordinary options button did not open.");
        for (String field : new String[] {"field_175357_i", "field_175356_r"}) {
            Object control = field(optionsScreen, field);
            require(control == null || (!buttons(optionsScreen).contains(control)
                    && !((List<?>) field(optionsScreen, "field_230705_e_")).contains(control)),
                    "The options screen retained a world difficulty control.");
        }
        setScreen(pause);
        Object share = button(pause, "menu.shareToLan");
        require(share != null && noWorldControls(pause), "The pause screen exposed world settings or cheats.");
        call(share, "func_230930_b_", NONE);
        require(screen() == null && Boolean.TRUE.equals(call(server(), "func_71344_c", NONE)),
                "One native share action did not publish and return to the game.");
        setScreen(pause);
        require(button(pause, REPORTS) != null && button(pause, "menu.shareToLan") == null && noWorldControls(pause),
                "The published world did not replace the native share action with Reports.");
        setScreen(null);
    }

    private static List<?> buttons(Object screen) throws Exception {
        return (List<?>) field(screen, "field_230710_m_");
    }

    private Object button(Object screen, String key) throws Exception {
        for (Object item : buttons(screen)) if (key.equals(key(call(item, "func_230458_i_", NONE)))) return item;
        return null;
    }

    private boolean noWorldControls(Object screen) throws Exception {
        for (Object item : buttons(screen)) {
            Object message = call(item, "func_230458_i_", NONE);
            String key = key(message);
            String text = String.valueOf(call(message, "getString", NONE)).toLowerCase(java.util.Locale.ROOT);
            if ("selectWorld.allowCommands".equals(key) || "menu.worldSettings".equals(key)
                    || text.contains("cheats") || text.contains("world settings")
                    || text.contains("\u0447\u0438\u0442\u044b") || text.contains("\u043d\u0430\u0441\u0442\u0440\u043e\u0439\u043a\u0438 \u043c\u0438\u0440\u0430")) {
                return false;
            }
        }
        return true;
    }

    private void checkPermissions(boolean attempt) throws Exception {
        Object server = schedule(client, this::server);
        Object host = schedule(client, () -> call(player(), "func_146103_bH", NONE));
        require("LanMcPackQa".equals(call(host, "getName", NONE)), "Unexpected host fixture profile.");
        Class<?> profile = type("com.mojang.authlib.GameProfile");
        Object guest = profile.getConstructor(UUID.class, String.class).newInstance(
                UUID.fromString("c6a97caf-1bd1-4ad8-b60a-38a9279ec4b4"), "LanMcQaGuest");
        schedule(server, () -> {
            Object players = call(server, "func_184103_al", NONE);
            if (attempt) call(players, "func_72387_b", new Class<?>[] {boolean.class}, true);
            for (Object tested : new Object[] {host, guest}) {
                require(Boolean.FALSE.equals(call(players, "func_152596_g", new Class<?>[] {profile}, tested))
                        && Integer.valueOf(0).equals(call(server, "func_211833_a", new Class<?>[] {profile}, tested)),
                        "Native player authority granted commands.");
            }
            Object data = call(server, "func_240793_aU_", NONE);
            require(Boolean.FALSE.equals(call(data, "func_76086_u", NONE))
                    && "SURVIVAL".equals(((Enum<?>) call(data, "func_76077_q", NONE)).name()),
                    "Saved world commands or game mode changed.");
            if (attempt) {
                Object actor = call(players, "func_152612_a", new Class<?>[] {String.class}, "LanMcPackQa");
                require(actor != null, "Native host player is missing.");
                Object source = call(actor, "func_195051_bN", NONE);
                Object command = call(server, "func_195571_aL", NONE);
                require(Integer.valueOf(0).equals(call(command, "func_197059_a",
                        new Class<?>[] {type("net.minecraft.command.CommandSource"), String.class},
                        source, "gamemode creative")), "Native host gamemode command succeeded.");
            }
            return null;
        });
        if (attempt) event("permissions", "\"actualPlayerList\":true,\"hostDenied\":true,"
                + "\"syntheticGuestDenied\":true,\"grantSetterDenied\":true,\"hostGameModeCommandDenied\":true");
    }

    private ChatProbe awaitChat() throws Exception {
        long until = Math.min(deadline, System.nanoTime() + TimeUnit.SECONDS.toNanos(75));
        ChatProbe probe;
        do {
            probe = schedule(client, this::readChat);
            if (probe.complete()) return probe;
            Thread.sleep(500);
        } while (System.nanoTime() < until);
        return probe;
    }

    private ChatProbe readChat() throws Exception {
        Object chat = call(field(client, "field_71456_v"), "func_146158_b", NONE);
        ChatProbe probe = new ChatProbe();
        for (Object entry : (List<?>) field(chat, "field_146252_h")) {
            Object message = call(entry, "func_238169_a_", NONE);
            String key = key(message);
            if (PORT.equals(key)) probe.portLines++;
            if (READY.equals(key)) {
                probe.readyLines++;
                inspectActions(message, probe, 0);
            }
        }
        return probe;
    }

    private void inspectActions(Object message, ChatProbe probe, int depth) throws Exception {
        if (depth > 8) return;
        Object style = call(message, "func_150256_b", NONE);
        Object click = call(style, "func_150235_h", NONE);
        Object color = call(style, "func_240711_a_", NONE);
        if (click != null && color != null) {
            String action = String.valueOf(call(click, "func_150668_b", NONE));
            int rgb = (Integer) call(color, "func_240742_a_", NONE);
            probe.copy |= "/e4steam copy".equals(action) && rgb == 0x55FF55;
            probe.invite |= "/e4steam invite".equals(action) && rgb == 0x5555FF;
            probe.close |= "/e4steam stop".equals(action) && rgb == 0xFF5555;
        }
        for (Object child : (List<?>) call(message, "func_150253_a", NONE)) inspectActions(child, probe, depth + 1);
        if (key(message) != null) {
            for (Object value : (Object[]) call(message, "func_150271_j", NONE)) {
                if (type("net.minecraft.util.text.ITextComponent").isInstance(value)) inspectActions(value, probe, depth + 1);
            }
        }
    }

    private String key(Object message) throws Exception {
        return type(TRANSLATION).isInstance(message) ? (String) call(message, "func_150268_i", NONE) : null;
    }

    private Object screen() throws Exception { return field(client, "field_71462_r"); }
    private Object player() throws Exception { return field(client, "field_71439_g"); }
    private Object server() throws Exception { return call(client, "func_71401_C", NONE); }
    private Class<?> type(String name) throws Exception { return Class.forName(name, false, loader); }
    private void setScreen(Object next) throws Exception {
        call(client, "func_147108_a", new Class<?>[] {type(SCREEN + "Screen")}, next);
    }

    private static <T> T schedule(Object owner, Callable<T> operation) throws Exception {
        CompletableFuture<T> result = new CompletableFuture<>();
        ((Executor) owner).execute(() -> {
            try { result.complete(operation.call()); }
            catch (Throwable failure) { result.completeExceptionally(failure); }
        });
        return result.get(100, TimeUnit.SECONDS);
    }

    private void await(String phase, int seconds, Callable<Boolean> condition) throws Exception {
        long until = Math.min(deadline, System.nanoTime() + TimeUnit.SECONDS.toNanos(seconds));
        do {
            if (condition.call()) return;
            Thread.sleep(250);
        } while (System.nanoTime() < until);
        throw new IllegalStateException("Timed out: " + phase);
    }

    private static Path path(String key) throws Exception {
        return Paths.get(System.getProperty(PREFIX + key)).toRealPath();
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

    private static Object call(Object target, String name, Class<?>[] types, Object... arguments) throws Exception {
        return invoke(target.getClass(), target, name, types, arguments);
    }

    private static Object invoke(Class<?> owner, Object target, String name, Class<?>[] types, Object... arguments)
            throws Exception {
        Method method = null;
        try { method = owner.getMethod(name, types); }
        catch (NoSuchMethodException absent) {
            for (Class<?> type = owner; type != null && method == null; type = type.getSuperclass()) {
                try { method = type.getDeclaredMethod(name, types); }
                catch (NoSuchMethodException missing) { }
            }
        }
        if (method == null) throw new NoSuchMethodException(owner.getName() + "." + name);
        method.setAccessible(true);
        return method.invoke(target, arguments);
    }

    private void event(String kind, String fields) throws Exception {
        String record = "{\"kind\":\"" + kind + "\"," + fields + "}\n";
        Files.write(evidence, record.getBytes(StandardCharsets.UTF_8), StandardOpenOption.CREATE, StandardOpenOption.APPEND);
    }

    private static void require(boolean condition, String message) {
        if (!condition) throw new IllegalStateException(message);
    }

    private static String jsonString(String value) {
        StringBuilder quoted = new StringBuilder("\"");
        for (char character : value.toCharArray()) {
            if (character == '"' || character == '\\') quoted.append('\\').append(character);
            else if (character < ' ') quoted.append(String.format("\\u%04x", (int) character));
            else quoted.append(character);
        }
        return quoted.append('"').toString();
    }

    private static final class ChatProbe {
        int portLines;
        int readyLines;
        boolean copy;
        boolean invite;
        boolean close;

        boolean complete() {
            return portLines == 1 && readyLines == 1 && copy && invite && close;
        }

        String evidence() {
            return "\"portLines\":" + portLines + ",\"readyLines\":" + readyLines
                    + ",\"greenCopy\":" + copy + ",\"blueInvite\":" + invite + ",\"redClose\":" + close;
        }
    }
}
