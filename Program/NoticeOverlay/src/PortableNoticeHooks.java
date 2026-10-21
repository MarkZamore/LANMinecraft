package minecraft.portable.notice;

import java.io.Reader;
import java.io.Writer;
import java.lang.management.ManagementFactory;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.List;
import java.util.Properties;

/** Bootstrap-visible entry point. No file I/O takes place on the render thread. */
public final class PortableNoticeHooks {
    static final long LIFE_MS = 7000;
    private static volatile Notice[] notices = new Notice[0];
    static volatile Properties style = new Properties();
    private static volatile long lastFrame;
    private static volatile String backend = "waiting";
    private static volatile int renderedCount;
    private static volatile String failure = "";
    private static volatile int transformations;
    private static boolean started;
    private static final ThreadLocal<PortableNoticeRenderer> RENDERERS = new ThreadLocal<PortableNoticeRenderer>();

    private PortableNoticeHooks() { }

    public static synchronized void start() {
        if (started) return;
        started = true;
        Thread worker = new Thread(new Runnable() {
            @Override public void run() { poll(); }
        }, "LANMinecraft notification channel");
        worker.setDaemon(true);
        worker.setContextClassLoader(null);
        worker.start();
    }

    public static void transformed(String api) {
        transformations++;
        backend = api;
        System.out.println("[LauncherNotice] Presentation hook installed: " + api);
    }

    public static void failed(String context, Throwable error) {
        failure = context + ": " + error.getClass().getSimpleName() + ": " + String.valueOf(error.getMessage());
        System.err.println("[LauncherNotice] " + failure);
    }

    public static void present(Class<?> api, long window) {
        // This must never propagate through GLFW/Display or introduce input handling.
        try {
            PortableNoticeRenderer renderer = RENDERERS.get();
            if (renderer == null || !renderer.matches(api, window)) {
                renderer = new PortableNoticeRenderer(api, window);
                RENDERERS.set(renderer);
            }
            int count = renderer.present(notices, System.currentTimeMillis());
            if (count < 0) return;
            renderedCount = count;
            failure = "";
            lastFrame = System.currentTimeMillis();
            backend = window == 0 ? "lwjgl2" : "lwjgl3";
        } catch (Throwable error) {
            // Throttle retries: a renderer with an unsupported context is retried on
            // the next context, rather than reporting an error on every game frame.
            PortableNoticeRenderer renderer = RENDERERS.get();
            if (renderer != null) renderer.disable();
            if (failure.length() == 0) failed("Fullscreen rendering", error);
        }
    }

    private static void poll() {
        Path directory = Paths.get(System.getProperty("lanminecraft.notice.directory"));
        Path snapshot = directory.resolve("notices.properties");
        Path theme = directory.resolve("style.properties");
        long previousStamp = Long.MIN_VALUE;
        long previousLength = -1;
        long lastHeartbeat = 0;
        try {
            if (Files.isRegularFile(theme)) style = readProperties(theme);
        } catch (Exception error) { failed("Notification theme", error); }
        while (true) {
            try {
                if (Files.isRegularFile(snapshot)) {
                    long stamp = Files.getLastModifiedTime(snapshot).toMillis();
                    long length = Files.size(snapshot);
                    if (stamp != previousStamp || length != previousLength) {
                        Notice[] candidate = parse(readProperties(snapshot), System.currentTimeMillis());
                        notices = candidate;
                        previousStamp = stamp;
                        previousLength = length;
                    }
                }
                long now = System.currentTimeMillis();
                if (now - lastHeartbeat >= 500) {
                    Properties health = new Properties();
                    health.setProperty("timestampUnixMs", Long.toString(lastFrame));
                    health.setProperty("backend", backend);
                    health.setProperty("renderedCount", Integer.toString(renderedCount));
                    health.setProperty("pid", processId());
                    health.setProperty("transformations", Integer.toString(transformations));
                    health.setProperty("error", failure);
                    writeProperties(directory.resolve("heartbeat.properties"), health);
                    lastHeartbeat = now;
                }
            } catch (Exception error) {
                // A torn/replaced or invalid snapshot does not destroy the last good
                // message; its timestamp still expires it after seven seconds.
            }
            try { Thread.sleep(250); } catch (InterruptedException ended) { return; }
        }
    }

    static Notice[] parse(Properties properties, long now) {
        int count = Integer.parseInt(properties.getProperty("count", "0"));
        if (count < 0 || count > 3) throw new IllegalArgumentException("Invalid notification count");
        List<Notice> result = new ArrayList<Notice>();
        for (int index = 0; index < count; index++) {
            String prefix = index + ".";
            String title = properties.getProperty(prefix + "title", "");
            String body = properties.getProperty(prefix + "body", "");
            long created = Long.parseLong(properties.getProperty(prefix + "createdUnixMs", "0"));
            if (title.length() > 160 || body.length() > 320 || title.length() == 0 || created > now + 2000)
                throw new IllegalArgumentException("Invalid notification payload");
            if (created <= now && now - created <= LIFE_MS) result.add(new Notice(title, body, created));
        }
        return result.toArray(new Notice[result.size()]);
    }

    private static Properties readProperties(Path path) throws Exception {
        if (Files.size(path) > 32768) throw new IllegalArgumentException("Notification snapshot exceeds limit");
        Properties data = new Properties();
        try (Reader input = Files.newBufferedReader(path, StandardCharsets.UTF_8)) { data.load(input); }
        return data;
    }

    private static void writeProperties(Path target, Properties values) throws Exception {
        Path temporary = target.resolveSibling(target.getFileName() + ".agent-tmp");
        try (Writer output = Files.newBufferedWriter(temporary, StandardCharsets.UTF_8)) { values.store(output, null); }
        try { Files.move(temporary, target, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING); }
        catch (java.nio.file.AtomicMoveNotSupportedException unsupported) {
            Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING);
        }
    }

    private static String processId() {
        String runtime = ManagementFactory.getRuntimeMXBean().getName();
        int separator = runtime.indexOf('@');
        return separator > 0 ? runtime.substring(0, separator) : "0";
    }

    static final class Notice {
        final String title;
        final String body;
        final long created;
        Notice(String title, String body, long created) { this.title = title; this.body = body; this.created = created; }
        String key() { return title + '\n' + body; }
    }
}
