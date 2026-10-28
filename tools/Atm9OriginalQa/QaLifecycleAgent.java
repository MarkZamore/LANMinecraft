import java.io.File;
import java.io.IOException;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.time.Instant;
import java.util.*;
import java.util.concurrent.*;
import java.util.function.Function;

/** Test-only native lifecycle/observation requests. No bytecode transformation. */
public final class QaLifecycleAgent {
    private final Path root, game, evidence, ready, stop;
    private final String freshWorld;
    private boolean created, modsRecorded;
    private int menuSamples;

    private QaLifecycleAgent() throws Exception {
        root = Path.of(required("root")).toRealPath();
        boolean underArtifacts = false;
        for (Path component : root) if (component.toString().equalsIgnoreCase("artifacts")) underArtifacts = true;
        if (!underArtifacts) throw new SecurityException("QA root must stay below artifacts");
        if (!Files.isRegularFile(root.resolve(".atm9-original-qa.json")))
            throw new SecurityException("Not a marked original ATM9 QA fixture");
        game = child(required("game")); evidence = child(required("evidence"));
        ready = evidence.resolve("ready.flag"); stop = evidence.resolve("stop.flag");
        freshWorld = System.getProperty("lanminecraft.qa.freshWorld");
        if (freshWorld != null && !freshWorld.matches("ATM9Qa[A-Za-z0-9_-]{0,48}"))
            throw new SecurityException("Unsafe QA world name");
        if (freshWorld != null && Files.exists(game.resolve("saves").resolve(freshWorld), LinkOption.NOFOLLOW_LINKS))
            throw new SecurityException("Fresh QA world already exists");
        if (Files.exists(ready) || Files.exists(stop)) throw new SecurityException("Use fresh evidence markers");
        event("waiting_for_coordinator", Map.of("classTransformation", false,
            "gameReflectionBeforeReady", false, "freshWorldRequested", freshWorld != null));
    }

    public static void premain(String ignored, Instrumentation instrumentation) {
        try {
            var agent = new QaLifecycleAgent();
            var worker = new Thread(() -> agent.run(instrumentation), "ATM9-original-QA-lifecycle");
            worker.setDaemon(true); worker.start();
        } catch (Throwable failure) { System.err.println("[ATM9 Original QA] Agent refused: " + failure); }
    }

    private void run(Instrumentation instrumentation) {
        var originalContext = Thread.currentThread().getContextClassLoader();
        try {
            Class<?> minecraft = null;
            long deadline = System.nanoTime() + TimeUnit.MINUTES.toNanos(45);
            while (System.nanoTime() < deadline) {
                // Instrumentation enumeration and game lookup happen only after
                // the coordinator has observed a fully loaded native title menu.
                if (!Files.isRegularFile(ready, LinkOption.NOFOLLOW_LINKS)) { Thread.sleep(1000); continue; }
                if (minecraft == null) {
                    for (Class<?> loaded : instrumentation.getAllLoadedClasses())
                        if (loaded.getName().equals("net.minecraft.client.Minecraft")) {
                            Thread.currentThread().setContextClassLoader(loaded.getClassLoader());
                            minecraft = loaded; break;
                        }
                }
                if (minecraft != null) {
                    Object client = method(minecraft, "m_91087_").invoke(null);
                    if (client != null) {
                        var result = new CompletableFuture<Boolean>();
                        ((Executor) client).execute(() -> {
                            var previous = Thread.currentThread().getContextClassLoader();
                            try {
                                Thread.currentThread().setContextClassLoader(client.getClass().getClassLoader());
                                result.complete(inspect(client));
                            } catch (Throwable failure) { result.completeExceptionally(failure); }
                            finally { Thread.currentThread().setContextClassLoader(previous); }
                        });
                        // Native datapack loading/world creation can hold the
                        // client request for minutes; a short observation timeout
                        // must not terminate the lifecycle worker mid-creation.
                        while (!result.isDone()) {
                            if (System.nanoTime() >= deadline) throw new TimeoutException("Native client request exceeded QA deadline");
                            Thread.sleep(250);
                        }
                        if (result.get()) return;
                    }
                }
                Thread.sleep(3000);
            }
            event("observation_timeout", Map.of("worldOrSteamPassClaimed", false));
        } catch (Throwable failure) {
            while ((failure instanceof InvocationTargetException || failure instanceof ExecutionException) && failure.getCause() != null)
                failure = failure.getCause();
            try { event("agent_failure", Map.of("exception", failure.getClass().getName(), "message", String.valueOf(failure.getMessage()))); }
            catch (IOException ignored) { }
        } finally { Thread.currentThread().setContextClassLoader(originalContext); }
    }

    private boolean inspect(Object client) throws Exception {
        Class<?> mc = client.getClass();
        File actual = (File) field(mc, "f_91069_").get(client);
        if (!actual.toPath().toRealPath().equals(game)) throw new SecurityException("Wrong actual game directory");
        if (Files.isRegularFile(stop, LinkOption.NOFOLLOW_LINKS)) {
            event("native_stop_requested", Map.of("method", "Minecraft.m_91395_", "forcedKill", false));
            method(mc, "m_91395_").invoke(client);
            return true;
        }
        Object player = field(mc, "f_91074_").get(client);
        Object level = field(mc, "f_91073_").get(client);
        Object screen = field(mc, "f_91080_").get(client);
        Object overlay = method(mc, "m_91265_").invoke(client);
        Object server = method(mc, "m_91092_").invoke(client);
        Map<String, Object> state = new LinkedHashMap<>();
        state.put("playerPresent", player != null); state.put("levelPresent", level != null);
        state.put("screenClass", className(screen)); state.put("overlayClass", className(overlay));
        state.put("integratedServerPresent", server != null);
        if (server != null) {
            String aliases = System.getProperty("minecraft.portable.identity.isPublishedMethods", "m_6992_,isPublished");
            for (String alias : aliases.split(",")) try {
                state.put("serverPublished", method(server.getClass(), alias.trim()).invoke(server)); break;
            } catch (NoSuchMethodException ignored) { }
        }
        state.put("actualSteamGuestConnectionVerified", false);
        event("client_state", state);
        if (!modsRecorded) {
            Class<?> modList = Class.forName("net.minecraftforge.fml.ModList", false, mc.getClassLoader());
            Object list = modList.getMethod("get").invoke(null);
            var ids = new TreeMap<String, String>();
            for (Object info : (Iterable<?>) modList.getMethod("getMods").invoke(list)) {
                String id = String.valueOf(info.getClass().getMethod("getModId").invoke(info));
                String version = String.valueOf(info.getClass().getMethod("getVersion").invoke(info));
                if (ids.putIfAbsent(id, version) != null) throw new IllegalStateException("Duplicate loaded mod ID: " + id);
            }
            event("loaded_mod_ids", Map.of("count", ids.size(), "ids", ids)); modsRecorded = true;
        }
        if (freshWorld == null || created) return false;
        boolean title = screen != null && Class.forName("net.minecraft.client.gui.screens.TitleScreen", false, mc.getClassLoader()).isInstance(screen);
        if (!title || overlay != null) { menuSamples = 0; return false; }
        if (player != null || level != null || server != null) throw new SecurityException("Refusing fresh creation with active world");
        if (++menuSamples < 3) return false;
        createFresh(client);
        return false;
    }

    private void createFresh(Object client) throws Exception {
        if (Files.exists(game.resolve("saves").resolve(freshWorld), LinkOption.NOFOLLOW_LINKS))
            throw new SecurityException("Fresh world collision before native creation");
        ClassLoader loader = client.getClass().getClassLoader();
        Class<?> settings = Class.forName("net.minecraft.world.level.LevelSettings", false, loader);
        Class<?> gameType = Class.forName("net.minecraft.world.level.GameType", false, loader);
        Class<?> difficulty = Class.forName("net.minecraft.world.Difficulty", false, loader);
        Class<?> rules = Class.forName("net.minecraft.world.level.GameRules", false, loader);
        Class<?> dataConfig = Class.forName("net.minecraft.world.level.WorldDataConfiguration", false, loader);
        Class<?> optionsType = Class.forName("net.minecraft.world.level.levelgen.WorldOptions", false, loader);
        Class<?> registry = Class.forName("net.minecraft.core.RegistryAccess", false, loader);
        Class<?> presets = Class.forName("net.minecraft.world.level.levelgen.presets.WorldPresets", false, loader);
        Object nativeSettings = settings.getDeclaredConstructor(String.class, gameType, boolean.class, difficulty, boolean.class, rules, dataConfig)
            .newInstance(freshWorld, gameType.getField("CREATIVE").get(null), false, difficulty.getField("NORMAL").get(null), true,
                rules.getDeclaredConstructor().newInstance(), dataConfig.getField("f_244649_").get(null));
        Object nativeOptions = optionsType.getDeclaredMethod("m_247394_").invoke(null);
        Method normal = presets.getDeclaredMethod("m_246552_", registry);
        Function<Object, Object> dimensions = access -> {
            var previous = Thread.currentThread().getContextClassLoader();
            try { Thread.currentThread().setContextClassLoader(loader); return normal.invoke(null, access); }
            catch (ReflectiveOperationException failure) { throw new IllegalStateException(failure); }
            finally { Thread.currentThread().setContextClassLoader(previous); }
        };
        Object flows = method(client.getClass(), "m_231466_").invoke(client);
        Method create = flows.getClass().getDeclaredMethod("m_233157_", String.class, settings, optionsType, Function.class);
        created = true;
        event("native_fresh_create_requested", Map.of("world", freshWorld, "preset", "current registry NORMAL",
            "commands", true, "mode", "CREATIVE", "method", create.toGenericString(), "nbtTemplatePatched", false));
        create.invoke(flows, freshWorld, nativeSettings, nativeOptions, dimensions);
        event("native_fresh_create_returned", Map.of("joinedWorldVerified", false, "savedWorldVerified", false));
    }

    private Path child(String value) throws IOException {
        Path path = Path.of(value).toRealPath();
        if (!path.startsWith(root) || Files.isSymbolicLink(path)) throw new SecurityException("QA path escaped marked fixture");
        return path;
    }
    private static String required(String key) {
        String value = System.getProperty("lanminecraft.qa." + key);
        if (value == null || value.isBlank()) throw new IllegalArgumentException("Missing QA property " + key);
        return value;
    }
    private static Method method(Class<?> type, String name) throws NoSuchMethodException {
        for (Class<?> current = type; current != null; current = current.getSuperclass())
            try { Method value = current.getDeclaredMethod(name); value.setAccessible(true); return value; }
            catch (NoSuchMethodException ignored) { }
        throw new NoSuchMethodException(type.getName() + "." + name);
    }
    private static Field field(Class<?> type, String name) throws NoSuchFieldException {
        Field value = type.getDeclaredField(name); value.setAccessible(true); return value;
    }
    private static String className(Object value) { return value == null ? null : value.getClass().getName(); }
    private synchronized void event(String kind, Map<String, ?> values) throws IOException {
        var row = new LinkedHashMap<String, Object>(); row.put("kind", kind); row.put("utc", Instant.now().toString()); row.putAll(values);
        Files.writeString(evidence.resolve("observations.jsonl"), json(row) + "\n", StandardCharsets.UTF_8, StandardOpenOption.CREATE, StandardOpenOption.APPEND);
        System.out.println("[ATM9 Original QA] " + kind);
    }
    private static String json(Object value) {
        if (value == null) return "null";
        if (value instanceof Number || value instanceof Boolean) return value.toString();
        if (value instanceof Map<?, ?> map) {
            var out = new StringJoiner(",", "{", "}");
            for (var entry : map.entrySet()) out.add(json(entry.getKey().toString()) + ":" + json(entry.getValue()));
            return out.toString();
        }
        StringBuilder out = new StringBuilder("\"");
        for (char c : value.toString().toCharArray()) switch (c) {
            case '"' -> out.append("\\\""); case '\\' -> out.append("\\\\"); case '\n' -> out.append("\\n");
            case '\r' -> out.append("\\r"); case '\t' -> out.append("\\t");
            default -> { if (c < 32) out.append(String.format("\\u%04x", (int) c)); else out.append(c); }
        }
        return out.append('"').toString();
    }
}
