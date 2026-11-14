package lanminecraft.qa;

import java.io.File;
import java.io.IOException;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
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
import java.util.Optional;
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
    private static final String SKYBLOCK_PRESET = "skyblockbuilder:skyblock";

    private final Path game, evidence, world;
    private final String minecraftVersion;
    private final String worldName;
    private final String reopenLevelSha256;
    private final String expectedWindowTitle;
    private final String worldPreset;
    private final String expectedInitialItem;
    private final boolean requireDefaultWorldPreset;
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
            case "1.21.1" -> "LanMcSteamQa121";
            default -> throw new SecurityException("No reviewed Forge QA profile for " + minecraftVersion);
        };
        String suffix = System.getProperty(PREFIX + "worldSuffix", "");
        if (!suffix.matches("[A-Za-z0-9_-]{0,24}")) throw new SecurityException("Invalid test-world suffix.");
        worldName = worldPrefix + suffix;
        world = game.resolve("saves").resolve(worldName);
        reopenLevelSha256 = System.getProperty(PREFIX + "reopenLevelSha256", "");
        expectedWindowTitle = System.getProperty(PREFIX + "expectedWindowTitle", "");
        worldPreset = System.getProperty(PREFIX + "worldPreset", "");
        expectedInitialItem = System.getProperty(PREFIX + "expectedInitialItem", "");
        requireDefaultWorldPreset = Boolean.getBoolean(PREFIX + "requireDefaultWorldPreset");
        require(worldPreset.isEmpty() || (usesNamedClasses() || minecraftVersion.equals("1.20.1")) &&
            worldPreset.matches("[a-z0-9_]+:[a-z0-9_./-]+"),
            "World preset verification requires a reviewed client and a resource identifier.");
        require(expectedInitialItem.isEmpty() || minecraftVersion.equals("1.20.1") &&
            expectedInitialItem.matches("[a-z0-9_]+:[a-z0-9_./-]+"),
            "Initial item verification requires a reviewed client and a resource identifier.");
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
            waitUntil("title", 240, () -> onClient(this::completeStartup));
            checkWindowTitle("startup");
            event("loaded_mod_ids", onClient(this::readMods));
            onClient(() -> { openWorld(); return null; });
            waitUntil("world join", 180, () -> onClient(this::completeWorldJoin));
            event(reopenLevelSha256.isEmpty() ? "world_created" : "world_reopened",
                Map.of("gameMode", "SURVIVAL", "commands", false));
            checkWindowTitle("world_before_steam");
            if (usesNamedClasses() || !worldPreset.isEmpty()) recordWorldGeneration();

            onClient(() -> { beginPublish(); return null; });
            waitUntil("world published and settings gone", 20, () -> onClient(() ->
                Boolean.TRUE.equals(invoke(server(), runtimeName("m_6992_", "isPublished"))) && screen() == null));
            event("published", Map.of("nativeListener", true, "settingsClosedOnPublication", true));

            checkPermissions();
            Map<String, Object> pause = onClient(this::checkPauseAfter);
            event("pause_after_publish", pause);
            ChatProbe steamChat = waitForChat();
            event("chat", steamChat.evidence());
            checkWindowTitle("world_after_steam");
            if (steamChat.complete()) checkPublishedSteamMetadata();

            if (!expectedInitialItem.isEmpty()) checkInitialItem();
            saveWorld();
            require(Boolean.TRUE.equals(pause.get("reports")) && Boolean.TRUE.equals(pause.get("shareToLanGone")) &&
                Boolean.FALSE.equals(pause.get("worldSettingsOrCheats")),
                "Published pause menu did not replace Share to LAN with Reports.");
            event("complete", Map.of("nativeSteamHost", steamChat.complete() ? "PASS" : "FAIL", "guestConnection", "NOT RUN"));
        } catch (Exception failure) {
            Throwable detail = failure;
            while (detail.getCause() != null) detail = detail.getCause();
            try { event("failure", Map.of("exception", detail.getClass().getName(), "message", String.valueOf(detail.getMessage()))); }
            catch (IOException ignored) { }
        } finally {
            if (client != null) try {
                ((Executor) client).execute(() -> {
                    try { invoke(client, runtimeName("m_91395_", "stop")); }
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
                    .anyMatch(frame -> frame.getClassName().equals(MC) && frame.getMethodName().equals(runtimeName("m_91374_", "runTick")))) return false;
            for (Class<?> loaded : instrumentation.getAllLoadedClasses()) {
                if (!loaded.getName().equals(MC)) continue;
                client = invoke(loaded, null, runtimeName("m_91087_", "getInstance"), new Class<?>[0]);
                if (client == null) continue;
                gameLoader = loaded.getClassLoader();
                return true;
            }
            return false;
        });
    }

    private boolean titleReady() throws Exception {
        require(((File) field(client.getClass(), runtimeName("f_91069_", "gameDirectory")).get(client)).toPath().toRealPath().equals(game),
            "Wrong actual game directory.");
        return type(SCREENS + "TitleScreen").isInstance(screen()) &&
            invoke(client, runtimeName("m_91265_", "getOverlay")) == null && player() == null && server() == null;
    }

    private boolean completeStartup() throws Exception {
        if (titleReady()) return true;
        Object current = screen();
        if (current != null && current.getClass().getName().equals(SCREENS + "AccessibilityOnboardingScreen") &&
                invoke(client, runtimeName("m_91265_", "getOverlay")) == null) {
            // The ordinary first-run lifecycle only persists the isolated QA instance's options.
            invoke(current, runtimeName("m_7379_", "onClose"));
            event("accessibility_onboarding_completed", Map.of("nativeLifecycle", true));
        }
        if (!worldPreset.isEmpty() && current != null &&
                current.getClass().getName().equals("org.betterx.bclib.client.gui.screens.WelcomeScreen") &&
                invoke(client, runtimeName("m_91265_", "getOverlay")) == null) {
            activateNativeAction(current, "gui.proceed");
            event("bclib_onboarding_completed", Map.of("nativeButtonCallback", true));
        }
        return false;
    }

    private boolean completeWorldJoin() throws Exception {
        if (player() != null && server() != null) return true;
        Object current = screen();
        if (minecraftVersion.equals("1.20.1") && !worldPreset.isEmpty() &&
                type(SCREENS + "ConfirmScreen").isInstance(current) &&
                "selectWorld.warning.experimental.title".equals(translationKey(invoke(current, "m_96636_")))) {
            activateNativeAction(current, "gui.yes");
            event("experimental_world_confirmation", Map.of("nativeButtonCallback", true));
        }
        return false;
    }

    private void checkWindowTitle(String phase) throws Exception {
        if (expectedWindowTitle.isEmpty()) return;
        String title = onClient(this::readWindowTitle);
        boolean matches = expectedWindowTitle.equals(title);
        event("window_title", Map.of("phase", phase, "actual", title, "expected", expectedWindowTitle,
            "matches", matches, "nativeWindow", true));
        require(matches, "The native game window did not show only the card name at " + phase + ".");
    }

    private String readWindowTitle() throws Exception {
        long window = (Long) invoke(invoke(client, runtimeName("m_91268_", "getWindow")), runtimeName("m_85439_", "getWindow"));
        long hwnd = (Long) invoke(type("org.lwjgl.glfw.GLFWNativeWin32"), null,
            "glfwGetWin32Window", new Class<?>[] { long.class }, window);
        require(hwnd != 0L, "The game has no native window handle.");
        Object user32 = invoke(type("org.lwjgl.system.windows.User32"), null, "getLibrary", new Class<?>[0]);
        long read = (Long) invoke(type("org.lwjgl.system.APIUtil"), null, "apiGetFunctionAddress",
            new Class<?>[] { type("org.lwjgl.system.FunctionProvider"), String.class }, user32, "GetWindowTextW");
        ByteBuffer utf16 = ByteBuffer.allocateDirect(1024).order(ByteOrder.LITTLE_ENDIAN);
        long address = (Long) invoke(type("org.lwjgl.system.MemoryUtil"), null, "memAddress",
            new Class<?>[] { ByteBuffer.class }, utf16);
        int length = (Integer) invoke(type("org.lwjgl.system.JNI"), null, "callPPI",
            new Class<?>[] { long.class, long.class, int.class, long.class }, hwnd, address, 512, read);
        require(length > 0 && length < 511, "Invalid native window title length.");
        return utf16.asCharBuffer().subSequence(0, length).toString();
    }

    private Map<String, Object> readMods() throws Exception {
        Class<?> modList = type(runtimeName("net.minecraftforge.fml.ModList", "net.neoforged.fml.ModList"));
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
        Object worldFlows = legacyComponents() ? client : invoke(client, runtimeName("m_231466_", "createWorldOpenFlows"));
        if (!reopenLevelSha256.isEmpty()) {
            if (legacyComponents()) {
                invoke(client, "m_91200_", new Class<?>[] { String.class }, worldName);
            } else if (usesNamedClasses()) {
                Runnable onFailure = () -> { throw new IllegalStateException("The pinned QA world could not reopen."); };
                invoke(worldFlows, "openWorld", new Class<?>[] { String.class, Runnable.class }, worldName, onFailure);
            } else {
                invoke(worldFlows, "m_233133_", new Class<?>[] { type(SCREENS + "Screen"), String.class }, screen(), worldName);
            }
            return;
        }
        if (!worldPreset.isEmpty()) {
            createWorldThroughNativePreset();
            return;
        }
        Class<?> settings = type(LEVEL + "LevelSettings");
        Class<?> mode = type(LEVEL + "GameType");
        Class<?> difficulty = type("net.minecraft.world.Difficulty");
        Class<?> gameRules = type(LEVEL + "GameRules");
        boolean worldDimensions = minecraftVersion.equals("1.20.1") || usesNamedClasses();
        Class<?> dataPacks = type(LEVEL + (worldDimensions ? "WorldDataConfiguration" : "DataPackConfig"));
        Class<?> registry = type("net.minecraft.core.RegistryAccess");
        Object levelSettings = settings.getConstructor(String.class, mode, boolean.class, difficulty, boolean.class,
            gameRules, dataPacks).newInstance(worldName, mode.getField("SURVIVAL").get(null), false,
                difficulty.getField("NORMAL").get(null), false, gameRules.getConstructor().newInstance(),
                dataPacks.getField(worldDimensions ? runtimeName("f_244649_", "DEFAULT") : "f_45842_").get(null));
        if (worldDimensions) {
            Class<?> options = type(LEVEL + "levelgen.WorldOptions");
            Class<?> presets = type(LEVEL + "levelgen.presets.WorldPresets");
            Object generationOptions = invoke(options, null, runtimeName("m_247394_", "defaultWithRandomSeed"), new Class<?>[0]);
            Function<Object, Object> dimensions = access -> {
                Thread thread = Thread.currentThread();
                ClassLoader previous = thread.getContextClassLoader();
                thread.setContextClassLoader(gameLoader);
                try { return invoke(presets, null, runtimeName("m_246552_", "createNormalWorldDimensions"), new Class<?>[] { registry }, access); }
                catch (ReflectiveOperationException failure) { throw new IllegalStateException(failure); }
                finally { thread.setContextClassLoader(previous); }
            };
            if (usesNamedClasses()) {
                invoke(worldFlows, "createFreshLevel", new Class<?>[] { String.class, settings, options, Function.class, type(SCREENS + "Screen") },
                    worldName, levelSettings, generationOptions, dimensions, screen());
            } else {
                invoke(worldFlows, "m_233157_", new Class<?>[] { String.class, settings, options, Function.class },
                    worldName, levelSettings, generationOptions, dimensions);
            }
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

    private void createWorldThroughNativePreset() throws Exception {
        String selection = SCREENS + "worldselection.";
        Class<?> create = type(selection + "CreateWorldScreen");
        invoke(create, null, runtimeName("m_232896_", "openFresh"),
            new Class<?>[] { type(MC), type(SCREENS + "Screen") }, client, screen());
        Object form = screen();
        require(create.isInstance(form), "The native Create World form did not open.");
        Object state = invoke(form, runtimeName("m_267748_", "getUiState"));
        String getWorldType = runtimeName("m_267828_", "getWorldType");
        Object current = invoke(state, getWorldType);
        String defaultPreset = presetKey(current);
        if (!worldPreset.equals(defaultPreset)) {
            require(!requireDefaultWorldPreset, "The author default world preset differs from the expected preset.");
            Object selected = null;
            for (Object candidate : (List<?>) invoke(state, runtimeName("m_267815_", "getNormalPresetList"))) {
                if (worldPreset.equals(presetKey(candidate))) { selected = candidate; break; }
            }
            require(selected != null, "The native Create World form does not offer the expected preset.");
            invoke(state, runtimeName("m_267576_", "setWorldType"),
                new Class<?>[] { type(selection + "WorldCreationUiState$WorldTypeEntry") }, selected);
        }
        String selectedPreset = presetKey(invoke(state, getWorldType));
        require(worldPreset.equals(selectedPreset), "The chosen world preset did not persist in the native form.");
        invoke(state, runtimeName("m_267649_", "setName"), new Class<?>[] { String.class }, worldName);
        Class<?> mode = type(selection + "WorldCreationUiState$SelectedGameMode");
        invoke(state, runtimeName("m_267616_", "setGameMode"), new Class<?>[] { mode }, mode.getField("SURVIVAL").get(null));
        invoke(state, runtimeName("m_267601_", "setAllowCommands"), new Class<?>[] { boolean.class }, false);
        event("native_world_preset", Map.of("defaultPreset", defaultPreset, "selectedPreset", selectedPreset,
            "authorDefaultVerified", requireDefaultWorldPreset, "nativeButtonCallback", true));
        activateNativeAction(form, "selectWorld.create");
    }

    private String presetKey(Object entry) throws Exception {
        Object holder = invoke(entry, runtimeName("f_267398_", "preset"));
        if (holder == null) return "custom";
        Optional<?> key = (Optional<?>) invoke(holder, runtimeName("m_203543_", "unwrapKey"));
        return key.isEmpty() ? "custom" : invoke(key.get(), runtimeName("m_135782_", "location")).toString();
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
        require(Boolean.TRUE.equals(schedule((Executor) server, () -> invoke(server, runtimeName("m_195514_", "saveEverything"),
            new Class<?>[] { boolean.class, boolean.class, boolean.class }, false, true, false))),
            "Native saveEverything did not save the QA world.");
        event("world_saved", Map.of("levelDatSha256", levelDatSha256(), "levelDatBytes", Files.size(world.resolve("level.dat"))));
        // Shutdown saves normally after the run loop. An in-process disconnect
        // renders a forced progress frame and can block on hidden GL windows.
    }

    private void recordWorldGeneration() throws Exception {
        Object server = onClient(this::server);
        Map<String, Object> dimensions = schedule((Executor) server, () -> {
            Object overworld = invoke(server, runtimeName("m_129783_", "overworld"));
            Object nether = serverDimension(server, "f_46429_", "NETHER");
            if (SKYBLOCK_PRESET.equals(worldPreset)) verifySkyblockGenerator(overworld);
            return usesNamedClasses()
                ? Map.of("overworld", describeGeneration(overworld), "nether", describeGeneration(nether))
                : Map.of("overworld", describeGeneration(overworld), "nether", describeGeneration(nether),
                    "end", describeGeneration(serverDimension(server, "f_46430_", "END")));
        });
        event("native_world_generation", dimensions);
    }

    private Object serverDimension(Object server, String srgField, String namedField) throws Exception {
        Object key = type(LEVEL + "Level").getField(runtimeName(srgField, namedField)).get(null);
        return invoke(server, runtimeName("m_129880_", "getLevel"),
            new Class<?>[] { type("net.minecraft.resources.ResourceKey") }, key);
    }

    private void verifySkyblockGenerator(Object overworld) throws Exception {
        Object chunks = invoke(overworld, "getChunkSource");
        Object generator = invoke(chunks, "getGenerator");
        require(generator.getClass().getName().equals("de.melanx.skyblockbuilder.world.chunkgenerators.SkyblockNoiseBasedChunkGenerator"),
            "The active overworld did not use the expected skyblock generator.");
        Object randomState = invoke(chunks, "randomState");
        Class<?>[] columnTypes = { int.class, int.class, type(LEVEL + "LevelHeightAccessor"), type(LEVEL + "levelgen.RandomState") };
        Object column = invoke(generator, "getBaseColumn", columnTypes, 1024, -1024, overworld, randomState);
        int min = (Integer) invoke(overworld, "getMinBuildHeight");
        int max = (Integer) invoke(overworld, "getMaxBuildHeight");
        require(max > min && max - min <= 2048, "Unexpected skyblock vertical bounds.");
        for (int y = min; y < max; y++) {
            Object block = invoke(column, "getBlock", new Class<?>[] { int.class }, y);
            require(Boolean.TRUE.equals(invoke(block, "isAir")), "Skyblock base noise created a non-air block.");
        }
        event("native_skyblock_noise", Map.of("generator", generator.getClass().getName(), "baseColumnAir", true,
            "testedHeights", max - min, "loadedChunk", false));
    }

    private Map<String, Object> describeGeneration(Object level) throws Exception {
        Object chunks = invoke(level, runtimeName("m_7726_", "getChunkSource"));
        Object generator = invoke(chunks, runtimeName("m_8481_", "getGenerator"));
        Object biomeSource = invoke(generator, runtimeName("m_62218_", "getBiomeSource"));
        List<String> biomes = new ArrayList<>();
        for (Object holder : (Iterable<?>) invoke(biomeSource, runtimeName("m_207840_", "possibleBiomes"))) {
            var key = (Optional<?>) invoke(holder, runtimeName("m_203543_", "unwrapKey"));
            if (key.isPresent()) biomes.add(invoke(key.get(), runtimeName("m_135782_", "location")).toString());
        }
        biomes.sort(String::compareTo);
        return Map.of("generator", generator.getClass().getName(), "biomes", biomes);
    }

    private void checkInitialItem() throws Exception {
        Object server = onClient(this::server);
        waitUntil("expected native host item", 30, () -> {
            int slot = schedule((Executor) server, () -> initialItemSlot(server));
            if (slot < 0) return false;
            event("native_initial_item", Map.of("item", expectedInitialItem, "slot", slot, "present", true));
            return true;
        });
    }

    private int initialItemSlot(Object server) throws Exception {
        Object players = invoke(server, "m_6846_");
        Object host = invoke(players, "m_11255_", new Class<?>[] { String.class }, PLAYER_NAME);
        Object inventory = invoke(host, "m_150109_");
        Object registry = type("net.minecraft.core.registries.BuiltInRegistries").getField("f_257033_").get(null);
        int slots = (Integer) invoke(inventory, "m_6643_");
        require(slots > 0 && slots <= 256, "Unexpected native player inventory size.");
        for (int i = 0; i < slots; i++) {
            Object stack = invoke(inventory, "m_8020_", new Class<?>[] { int.class }, i);
            if (Boolean.TRUE.equals(invoke(stack, "m_41619_"))) continue;
            Object item = invoke(stack, "m_41720_");
            if (expectedInitialItem.equals(invoke(registry, "m_7981_", new Class<?>[] { Object.class }, item).toString())) {
                return i;
            }
        }
        return -1;
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
        invoke(action, runtimeName("m_5691_", "onPress"));
    }

    private Map<String, Object> checkPauseAfter() throws Exception {
        Object pause = type(SCREENS + "PauseScreen").getConstructor(boolean.class).newInstance(true);
        setScreen(pause);
        List<Object> reportActions = visibleActions(pause, "menu.playerReporting", "Жалобы", "Player Reporting");
        boolean reports = reportActions.size() == 1 && isActive(reportActions.get(0));
        boolean shareGone = visibleActions(pause, "menu.shareToLan").isEmpty();
        boolean noWorldControls = noWorldControls(pause);
        Map<String, Object> result = new LinkedHashMap<>();
        result.put("reports", reports);
        result.put("shareToLanGone", shareGone);
        result.put("worldSettingsOrCheats", !noWorldControls);
        if (!reports || !shareGone || !noWorldControls) {
            List<Map<String, Object>> widgets = new ArrayList<>();
            for (Object widget : (List<?>) invoke(pause, runtimeName("m_6702_", "children"))) {
                if (!type("net.minecraft.client.gui.components.AbstractWidget").isInstance(widget)) continue;
                Object label = invoke(widget, runtimeName("m_6035_", "getMessage"));
                String key = translationKey(label);
                widgets.add(Map.of("class", widget.getClass().getName(), "key", key == null ? "" : key,
                    "visible", field(widget.getClass(), runtimeName("f_93624_", "visible")).get(widget),
                    "active", isActive(widget)));
            }
            result.put("widgets", widgets);
        }
        setScreen(null);
        return result;
    }

    private void checkPermissions() throws Exception {
        Object server = onClient(this::server);
        Object host = onClient(() -> invoke(player(), runtimeName("m_36316_", "getGameProfile")));
        Class<?> profile = type("com.mojang.authlib.GameProfile");
        require(PLAYER_NAME.equals(invoke(host, "getName")), "Wrong native QA player.");
        Object guest = profile.getConstructor(UUID.class, String.class).newInstance(
            UUID.fromString("d96a4354-8ff0-4b76-8202-6835ef952efb"), "LanMcQaGuest");
        Map<String, Boolean> result = schedule((Executor) server, () -> {
            Object players = invoke(server, runtimeName("m_6846_", "getPlayerList"));
            Object data = invoke(server, runtimeName("m_129910_", "getWorldData"));
            String isOp = runtimeName("m_11303_", "isOp");
            boolean hostBefore = Boolean.TRUE.equals(invoke(players, isOp, new Class<?>[] { profile }, host));
            boolean guestBefore = Boolean.TRUE.equals(invoke(players, isOp, new Class<?>[] { profile }, guest));
            invoke(players, runtimeName("m_11284_", "setAllowCommandsForAllPlayers"), new Class<?>[] { boolean.class }, true);
            boolean hostAfter = Boolean.TRUE.equals(invoke(players, isOp, new Class<?>[] { profile }, host));
            boolean guestAfter = Boolean.TRUE.equals(invoke(players, isOp, new Class<?>[] { profile }, guest));
            boolean stored = Boolean.TRUE.equals(invoke(data, runtimeName("m_5468_", "isAllowCommands")));
            boolean survival = "SURVIVAL".equals(((Enum<?>) invoke(data, runtimeName("m_5464_", "getGameType"))).name());
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

    private void checkPublishedSteamMetadata() throws Exception {
        Class<?> steamRuntime = type("link.e4steam.steam.SteamRuntime");
        Object runtime = invoke(steamRuntime, null, "get", new Class<?>[0]);
        boolean[] matches = new boolean[3];
        waitUntil("Steam lobby display metadata", 45, () -> {
            CompletableFuture<?> read = (CompletableFuture<?>) invoke(runtime, "submitSteamTask",
                new Class<?>[] { Callable.class }, (Callable<Boolean>) () -> {
                    Object host = field(steamRuntime, "hostRegistration").get(runtime);
                    Object manager = field(steamRuntime, "lobbyManager").get(runtime);
                    if (host == null || manager == null) return false;
                    long id = (Long) field(manager.getClass(), "hostLobbyId").get(manager);
                    if (id == 0 || field(manager.getClass(), "hostLobbyOwner").get(manager) != invoke(host, "owner")) return false;
                    Class<?> steamId = type("com.codedisaster.steamworks.SteamID");
                    Object lobby = invoke(steamId, null, "createFromNativeHandle", new Class<?>[] { long.class }, id);
                    Object matchmaking = field(manager.getClass(), "matchmaking").get(manager);
                    Class<?>[] signature = { steamId, String.class };
                    matches[0] = worldName.equals(invoke(matchmaking, "getLobbyData", signature, lobby, "lanminecraft_world"));
                    matches[1] = PLAYER_NAME.equals(invoke(matchmaking, "getLobbyData", signature, lobby, "lanminecraft_player"));
                    matches[2] = "1".equals(invoke(matchmaking, "getLobbyData", signature, lobby, "lanminecraft_display"));
                    return matches[0] && matches[1] && matches[2];
                });
            return Boolean.TRUE.equals(read.get(8, TimeUnit.SECONDS));
        });
        event("steam_lobby_metadata", Map.of("actualSteamLobby", true, "matchesWorldName", matches[0],
            "matchesLauncherPlayer", matches[1], "displayFlag", matches[2], "secondAccountDiscovery", "NOT RUN"));
    }

    private ChatProbe readChat() throws Exception {
        Object gui = field(client.getClass(), runtimeName("f_91065_", "gui")).get(client);
        // Read the native local chat. Mods may redirect getChat() to a
        // different selected channel and forward system messages to this HUD.
        Object chat = field(gui.getClass(), runtimeName("f_92988_", "chat")).get(gui);
        var probe = new ChatProbe();
        for (Object entry : (List<?>) field(chat.getClass(), runtimeName("f_93760_", "allMessages")).get(chat)) {
            Object message = invoke(entry, legacyComponents() ? "m_90793_" : runtimeName("f_240363_", "content"));
            if ("commands.publish.started".equals(translationKey(message))) probe.portLines++;
            if (!"text.e4steam_minecraft.domainAssigned".equals(translationKey(message))) continue;
            probe.readyLines++;
            inspectActions(message, probe, 0);
        }
        return probe;
    }

    private void inspectActions(Object component, ChatProbe probe, int depth) throws Exception {
        require(depth < 20, "Chat component is too deeply nested.");
        Object style = invoke(component, runtimeName("m_7383_", "getStyle"));
        Object click = invoke(style, runtimeName("m_131182_", "getClickEvent"));
        if (click != null) {
            String action = ((Enum<?>) invoke(click, runtimeName("m_130622_", "getAction"))).name();
            String value = (String) invoke(click, runtimeName("m_130623_", "getValue"));
            Object color = invoke(style, runtimeName("m_131135_", "getColor"));
            int rgb = color == null ? -1 : (Integer) invoke(color, runtimeName("m_131265_", "getValue"));
            if ("COPY_TO_CLIPBOARD".equals(action) && value.endsWith(".steam") && rgb == 0x55ff55) probe.address = true;
            if ("RUN_COMMAND".equals(action) && "/e4steam invite".equals(value) && rgb == 0x5555ff) probe.invite = true;
            if ("RUN_COMMAND".equals(action) && "/e4steam stop".equals(value) && rgb == 0xff5555) probe.stop = true;
        }
        Object contents = translatableContents(component);
        if (contents != null) {
            for (Object arg : (Object[]) invoke(contents, legacyComponents() ? "m_131329_" : runtimeName("m_237523_", "getArgs"))) {
                if (type(CHAT + "Component").isInstance(arg)) inspectActions(arg, probe, depth + 1);
            }
        }
        for (Object sibling : (List<?>) invoke(component, runtimeName("m_7360_", "getSiblings"))) inspectActions(sibling, probe, depth + 1);
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
        for (Object widget : (List<?>) invoke(pause, runtimeName("m_6702_", "children"))) {
            if (!type("net.minecraft.client.gui.components.AbstractWidget").isInstance(widget)) continue;
            Object label = invoke(widget, runtimeName("m_6035_", "getMessage"));
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
        for (Object widget : (List<?>) invoke(pause, runtimeName("m_6702_", "children"))) {
            if (!type("net.minecraft.client.gui.components.AbstractWidget").isInstance(widget)) continue;
            if (!Boolean.TRUE.equals(field(widget.getClass(), runtimeName("f_93624_", "visible")).get(widget))) continue;
            Object label = invoke(widget, runtimeName("m_6035_", "getMessage"));
            if (key.equals(translationKey(label)) || translations.contains(invoke(label, "getString"))) actions.add(widget);
        }
        return actions;
    }

    private void activateNativeAction(Object screen, String key) throws Exception {
        List<Object> actions = visibleActions(screen, key);
        require(actions.size() == 1 && isActive(actions.get(0)), "The native action was not available: " + key);
        invoke(actions.get(0), runtimeName("m_5691_", "onPress"));
    }

    private boolean isActive(Object widget) throws ReflectiveOperationException {
        return Boolean.TRUE.equals(field(widget.getClass(), runtimeName("f_93623_", "active")).get(widget));
    }

    private String translationKey(Object component) throws Exception {
        Object contents = translatableContents(component);
        return contents == null ? null : (String) invoke(contents, legacyComponents() ? "m_131328_" : runtimeName("m_237508_", "getKey"));
    }

    private Object translatableContents(Object component) throws Exception {
        if (legacyComponents()) return type(CHAT + "TranslatableComponent").isInstance(component) ? component : null;
        Object contents = invoke(component, runtimeName("m_214077_", "getContents"));
        return type(CHAT + "contents.TranslatableContents").isInstance(contents) ? contents : null;
    }

    private boolean legacyComponents() { return minecraftVersion.equals("1.18.2"); }
    private boolean usesNamedClasses() { return minecraftVersion.equals("1.21.1"); }
    private String runtimeName(String srg, String named) { return usesNamedClasses() ? named : srg; }

    private void setScreen(Object screen) throws ReflectiveOperationException {
        invoke(client, runtimeName("m_91152_", "setScreen"), new Class<?>[] { type(SCREENS + "Screen") }, screen);
    }
    private Object screen() throws ReflectiveOperationException { return field(client.getClass(), runtimeName("f_91080_", "screen")).get(client); }
    private Object player() throws ReflectiveOperationException { return field(client.getClass(), runtimeName("f_91074_", "player")).get(client); }
    private Object server() throws ReflectiveOperationException { return invoke(client, runtimeName("m_91092_", "getSingleplayerServer")); }
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
        if (value instanceof Iterable<?> values) {
            var entries = new StringJoiner(",", "[", "]");
            values.forEach(entry -> entries.add(json(entry)));
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
