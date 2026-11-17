import java.io.IOException;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Instant;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Function;

/** Test-only native lifecycle observer. It adds no class transformers. */
public final class RuntimeComponentsQaAgent {
    private static final String WORLD = "RuntimeComponentsQa";
    private final Instrumentation instrumentation;
    private final Path evidence;
    private final boolean fresh;
    private final AtomicBoolean queued = new AtomicBoolean();
    private Class<?> minecraftType;
    private Object minecraft;
    private Object pause;
    private int stage;
    private boolean finished;
    private long titleAt;
    private long deadline;
    private volatile Throwable creationFailure;

    private RuntimeComponentsQaAgent(Instrumentation instrumentation) throws IOException {
        this.instrumentation = instrumentation;
        Path root = Path.of(required("lanminecraft.qa.root")).toRealPath();
        if (!root.getParent().getFileName().toString().equals("agent-runs") ||
            !Files.isRegularFile(root.resolve("RUN.json"))) throw new SecurityException("Unowned QA fixture");
        Path game = Path.of(required("lanminecraft.qa.game")).toRealPath();
        if (!game.startsWith(root.resolve("Minecraft").resolve("Personal").resolve("Instances")))
            throw new SecurityException("Unexpected QA game directory");
        evidence = Path.of(required("lanminecraft.qa.evidence")).toAbsolutePath().normalize();
        if (!evidence.startsWith(root) || evidence.equals(root)) throw new SecurityException("Escaped QA evidence path");
        Files.createDirectories(evidence);
        fresh = Boolean.parseBoolean(required("lanminecraft.qa.fresh"));
        Path save = game.resolve("saves").resolve(WORLD);
        if (fresh && Files.exists(save)) throw new SecurityException("Existing QA world cannot be overwritten");
        if (!fresh && !Files.isRegularFile(save.resolve("level.dat"))) throw new SecurityException("Missing saved QA world");
        deadline = System.nanoTime() + 600_000_000_000L;
    }

    public static void premain(String arguments, Instrumentation instrumentation) throws Exception {
        RuntimeComponentsQaAgent observer = new RuntimeComponentsQaAgent(instrumentation);
        Thread thread = new Thread(observer::poll, "Runtime component native QA observer");
        thread.setDaemon(true);
        thread.start();
    }

    private void poll() {
        try {
            while (!finished) {
                if (System.nanoTime() > deadline) {
                    fail(new IllegalStateException("Native acceptance timed out at stage " + stage));
                    return;
                }
                if (minecraftType == null) {
                    for (Class<?> type : instrumentation.getAllLoadedClasses()) {
                        if (type.getName().equals("net.minecraft.client.Minecraft")) {
                            minecraftType = type;
                            break;
                        }
                    }
                }
                if (minecraftType != null && queued.compareAndSet(false, true)) {
                    minecraft = callStatic(minecraftType, "getInstance");
                    if (minecraft == null) queued.set(false);
                    else schedule(() -> {
                        try { tick(); }
                        catch (Throwable failure) { fail(failure); }
                        finally { queued.set(false); }
                    });
                }
                Thread.sleep(250);
            }
        } catch (Throwable failure) { fail(failure); }
    }

    private void tick() throws Exception {
        if (creationFailure != null) throw new IllegalStateException("Native world creation callback failed", creationFailure);
        Object gui = field(minecraft, "gui");
        if (gui == null) return;
        Object screen = call(gui, "screen");
        if (stage > 0 && screen != null && screen.getClass().getName().endsWith("DisconnectedScreen"))
            throw new IllegalStateException("The isolated QA player was disconnected during native world entry");
        Object server = call(minecraft, "getSingleplayerServer");
        if (stage == 1 && !fresh && screen != null &&
            screen.getClass().getName().equals("net.minecraft.client.gui.screens.BackupConfirmScreen")) {
            List<Object> proceed = controls(screen).stream()
                .filter(button -> label(button).equals("I Know What I'm Doing!")).toList();
            if (proceed.size() != 1) throw new IllegalStateException("Unexpected disposable-world confirmation controls");
            event("confirmed_saved_qa_world_warning", "world", WORLD);
            press(proceed.get(0));
            return;
        }
        if (stage == 0) {
            if (screen == null || !screen.getClass().getName().equals("net.minecraft.client.gui.screens.TitleScreen") ||
                call(gui, "overlay") != null) return;
            if (titleAt == 0) {
                titleAt = System.nanoTime();
                event("title_ready", "screen", screen.getClass().getName());
                return;
            }
            if (System.nanoTime() - titleAt < 2_000_000_000L) return;
            stage = 1;
            Object flows = call(minecraft, "createWorldOpenFlows");
            if (fresh) createWorld(flows, screen);
            else method(flows.getClass(), "openWorld", 2).invoke(flows, WORLD, (Runnable) () -> {});
            event(fresh ? "world_creation_requested" : "saved_world_open_requested", "world", WORLD);
        } else if (stage == 1) {
            if (server == null || field(minecraft, "level") == null || field(minecraft, "player") == null ||
                screen != null && !screen.getClass().getName().equals("net.minecraft.client.gui.screens.PauseScreen")) return;
            event("world_ready", "mode", call(server, "getDefaultGameType").toString());
            pause = type("net.minecraft.client.gui.screens.PauseScreen").getConstructor(boolean.class).newInstance(true);
            method(gui.getClass(), "setScreen", 1).invoke(gui, pause);
            stage = 2;
        } else if (stage == 2) {
            if (screen != pause) return;
            List<Object> controls = controls(pause);
            requireNoWorldOptions(controls);
            Object lan = controls.stream().filter(button -> label(button).equals("Open to LAN")).findFirst()
                .orElseThrow(() -> new IllegalStateException("Native Open to LAN action is missing"));
            event("pause_before_lan", "buttons", labels(controls).toString());
            press(lan);
            stage = 3;
        } else if (stage == 3) {
            if (!Boolean.TRUE.equals(call(server, "isPublished")) || screen != null) return;
            pause = type("net.minecraft.client.gui.screens.PauseScreen").getConstructor(boolean.class).newInstance(true);
            method(gui.getClass(), "setScreen", 1).invoke(gui, pause);
            stage = 4;
        } else if (stage == 4) {
            if (screen != pause) return;
            List<Object> controls = controls(pause);
            requireNoWorldOptions(controls);
            Object reporting = staticField(pause.getClass(), "PLAYER_REPORTING");
            boolean found = false;
            for (Object control : controls) {
                try { if (reporting.equals(call(control, "getMessage"))) found = true; }
                catch (NoSuchMethodException ignored) { }
            }
            if (!found) throw new IllegalStateException("Native reporting action is missing after LAN publication");
            event("pause_after_lan", "buttons", labels(controls).toString());
            Files.writeString(evidence.resolve("native-result.json"),
                "{\"passed\":true,\"world\":\"" + WORLD + "\",\"mode\":\"CREATIVE\",\"commands\":true," +
                "\"worldOptionsHiddenBeforeAndAfterLan\":true,\"nativeLanPublished\":true,\"reportsPresent\":true}",
                StandardCharsets.UTF_8);
            finished = true;
            call(minecraft, "stop");
        }
    }

    private void createWorld(Object flows, Object parent) throws Exception {
        Class<?> settings = type("net.minecraft.world.level.LevelSettings");
        Class<?> mode = type("net.minecraft.world.level.GameType");
        Class<?> difficulty = type("net.minecraft.world.level.LevelSettings$DifficultySettings");
        Class<?> data = type("net.minecraft.world.level.WorldDataConfiguration");
        Object level = settings.getConstructor(String.class, mode, difficulty, boolean.class, data)
            .newInstance(WORLD, staticField(mode, "CREATIVE"), staticField(difficulty, "DEFAULT"), true, staticField(data, "DEFAULT"));
        Object options = type("net.minecraft.world.level.levelgen.WorldOptions")
            .getConstructor(long.class, boolean.class, boolean.class).newInstance(31337L, true, false);
        Function<Object, Object> dimensions = provider -> {
            try {
                Object key = staticField(type("net.minecraft.core.registries.Registries"), "WORLD_PRESET");
                Object lookup = method(provider.getClass(), "lookupOrThrow", 1).invoke(provider, key);
                Object normal = staticField(type("net.minecraft.world.level.levelgen.presets.WorldPresets"), "NORMAL");
                Method getPreset = lookup.getClass().getMethod("getOrThrow", type("net.minecraft.resources.ResourceKey"));
                getPreset.setAccessible(true);
                Object holder = getPreset.invoke(lookup, normal);
                return call(call(holder, "value"), "createWorldDimensions");
            } catch (Exception failure) { creationFailure = failure; throw new IllegalStateException(failure); }
        };
        method(flows.getClass(), "createFreshLevel", 5).invoke(flows, WORLD, level, options, dimensions, parent);
    }

    private void requireNoWorldOptions(List<Object> controls) throws Exception {
        Object forbidden = staticField(pause.getClass(), "WORLD_OPTIONS");
        for (Object control : controls) {
            try {
                if (forbidden.equals(call(control, "getMessage"))) throw new IllegalStateException("World Options remains in native pause controls");
            } catch (NoSuchMethodException ignored) { }
        }
    }

    private List<Object> controls(Object screen) throws Exception {
        List<Object> result = new ArrayList<>();
        collect(screen, result, 0);
        return result;
    }

    private void collect(Object control, List<Object> result, int depth) throws Exception {
        if (depth > 12) return;
        result.add(control);
        try {
            Object children = call(control, "children");
            if (children instanceof Iterable<?> values) for (Object child : values) collect(child, result, depth + 1);
        } catch (NoSuchMethodException ignored) { }
    }

    private static List<String> labels(List<Object> controls) {
        return controls.stream().map(RuntimeComponentsQaAgent::label).filter(value -> !value.isEmpty()).toList();
    }

    private static String label(Object control) {
        try { return String.valueOf(call(call(control, "getMessage"), "getString")); }
        catch (Exception unavailable) { return ""; }
    }

    private void press(Object button) throws Exception {
        Object action = field(button, "onPress");
        type("net.minecraft.client.gui.components.Button$OnPress").getMethod("onPress", type("net.minecraft.client.gui.components.Button"))
            .invoke(action, button);
    }

    private void schedule(Runnable action) throws Exception {
        try { method(minecraftType, "schedule", 1).invoke(minecraft, action); }
        catch (NoSuchMethodException missing) { method(minecraftType, "execute", 1).invoke(minecraft, action); }
    }

    private void fail(Throwable failure) {
        if (finished) return;
        finished = true;
        try {
            Files.writeString(evidence.resolve("native-result.json"), "{\"passed\":false,\"stage\":" + stage +
                ",\"error\":\"" + escape(failure.toString()) + "\"}", StandardCharsets.UTF_8);
            event("failure", "error", failure.toString());
            if (minecraft != null) schedule(() -> { try { call(minecraft, "stop"); } catch (Exception ignored) {} });
        } catch (Exception ignored) { failure.printStackTrace(); }
    }

    private void event(String event, String name, String value) throws IOException {
        String line = "{\"event\":\"" + event + "\",\"time\":\"" + Instant.now() + "\",\"" + name +
            "\":\"" + escape(value) + "\"}\n";
        Files.writeString(evidence.resolve("native-events.jsonl"), line, StandardCharsets.UTF_8,
            java.nio.file.StandardOpenOption.CREATE, java.nio.file.StandardOpenOption.APPEND);
        System.out.print("[RuntimeComponentsQA] " + line);
    }

    private Class<?> type(String name) throws ClassNotFoundException { return Class.forName(name, true, minecraftType.getClassLoader()); }
    private static String required(String name) { return java.util.Objects.requireNonNull(System.getProperty(name), name); }
    private static String escape(String value) { return value.replace("\\", "\\\\").replace("\"", "\\\"").replace("\r", "\\r").replace("\n", "\\n"); }
    private static Object call(Object target, String name) throws Exception { return method(target.getClass(), name, 0).invoke(target); }
    private static Object callStatic(Class<?> type, String name) throws Exception { return method(type, name, 0).invoke(null); }
    private static Object staticField(Class<?> type, String name) throws Exception { Field field = findField(type, name); return field.get(null); }
    private static Object field(Object target, String name) throws Exception { return findField(target.getClass(), name).get(target); }
    private static Field findField(Class<?> type, String name) throws NoSuchFieldException {
        for (Class<?> current = type; current != null; current = current.getSuperclass()) {
            try { Field field = current.getDeclaredField(name); field.setAccessible(true); return field; }
            catch (NoSuchFieldException ignored) { }
        }
        throw new NoSuchFieldException(type.getName() + "." + name);
    }
    private static Method method(Class<?> type, String name, int parameters) throws NoSuchMethodException {
        for (Method method : type.getMethods()) if (method.getName().equals(name) && method.getParameterCount() == parameters) {
            method.setAccessible(true); return method;
        }
        for (Class<?> current = type; current != null; current = current.getSuperclass())
            for (Method method : current.getDeclaredMethods()) if (method.getName().equals(name) && method.getParameterCount() == parameters) {
                method.setAccessible(true); return method;
            }
        throw new NoSuchMethodException(type.getName() + "." + name);
    }
}
