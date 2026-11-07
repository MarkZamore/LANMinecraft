package minecraft.portable.notice;

import java.awt.image.BufferedImage;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.WeakHashMap;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
import javax.imageio.ImageIO;
import javax.imageio.ImageReader;
import javax.imageio.stream.MemoryCacheImageInputStream;

/** Window creation only: no Minecraft classes, polling, input or game settings. */
public final class PortableNoticeWindowHooks {
    private static final int VISIBLE = 0x00020004;
    private static final int FOCUSED = 0x00020001;
    private static final int FOCUS_ON_SHOW = 0x0002000c;
    private static final Map<Class<?>, Modern> MODERN = new WeakHashMap<Class<?>, Modern>();
    private static List<Icon> icons;
    private static boolean failureReported;
    private PortableNoticeWindowHooks() { }

    public static String title(String original) {
        String configured = System.getProperty("lanminecraft.window.title", "");
        return configured.length() == 0 ? original : configured;
    }

    private static synchronized Modern modern(Class<?> glfw) throws Exception {
        Modern state = MODERN.get(glfw);
        if (state == null) { state = new Modern(glfw); MODERN.put(glfw, state); }
        return state;
    }

    public static void hint(Class<?> glfw, int hint, int value) {
        if (hint != VISIBLE && hint != FOCUSED) return;
        try {
            Modern state = modern(glfw);
            if (!state.applying) {
                if (hint == VISIBLE) state.visible = value != 0;
                else state.focused = value != 0;
            }
        } catch (Throwable failure) { failed(failure); }
    }

    public static void defaultHints(Class<?> glfw) { hint(glfw, VISIBLE, 1); hint(glfw, FOCUSED, 1); }

    public static long beforeCreate(Class<?> glfw, long originalTitle) {
        try {
            Modern state = modern(glfw);
            Class<?> memory = Class.forName("org.lwjgl.system.MemoryUtil", true, glfw.getClassLoader());
            long pointer = ((Number)memory.getMethod("memAddress", ByteBuffer.class).invoke(null, state.title)).longValue();
            state.pendingVisible = state.visible;
            state.pendingFocused = state.focused;
            state.windowHint(0);
            state.pending = true;
            return pointer;
        } catch (Throwable failure) { failed(failure); return originalTitle; }
    }

    public static long created(long window, Class<?> glfw) {
        Modern state = null;
        try {
            state = modern(glfw);
            if (window != 0) state.applyIcon(window);
        } catch (Throwable failure) { failed(failure); }
        finally {
            if (state != null && state.pending) {
                boolean show = state.pendingVisible;
                state.pending = false;
                try { state.windowHint(show ? 1 : 0); } catch (Throwable failure) { failed(failure); }
                if (window != 0 && show) {
                    try { state.showWindow(window); }
                    catch (Throwable failure) { failed(failure); }
                }
            }
        }
        return window;
    }

    public static long titlePointer(Class<?> glfw, long originalTitle) {
        try {
            Modern state = modern(glfw);
            Class<?> memory = Class.forName("org.lwjgl.system.MemoryUtil", true, glfw.getClassLoader());
            return ((Number)memory.getMethod("memAddress", ByteBuffer.class).invoke(null, state.title)).longValue();
        } catch (Throwable failure) { failed(failure); return originalTitle; }
    }

    public static Object iconBuffer(Class<?> glfw, Object original) {
        try { return modern(glfw).images(); }
        catch (Throwable failure) { failed(failure); return original; }
    }

    public static ByteBuffer[] legacyIcons(ByteBuffer[] original) {
        try {
            List<Icon> source = icons();
            ByteBuffer[] pixels = new ByteBuffer[source.size()];
            for (int i = 0; i < pixels.length; i++) pixels[i] = source.get(i).pixels.duplicate();
            return pixels;
        } catch (Throwable failure) { failed(failure); return original; }
    }

    /** LWJGL 2 shows its native window before Display applies its cached icon. */
    public static void beforeLegacyShow(Object implementation) {
        try {
            java.lang.reflect.Method setTitle = implementation.getClass().getMethod("setTitle", String.class);
            setTitle.setAccessible(true);
            setTitle.invoke(implementation, title("Minecraft"));
            List<Icon> source = icons();
            ByteBuffer[] pixels = new ByteBuffer[source.size()];
            for (int i = 0; i < pixels.length; i++) pixels[i] = source.get(i).pixels.duplicate();
            java.lang.reflect.Method setIcon = implementation.getClass().getMethod("setIcon", ByteBuffer[].class);
            setIcon.setAccessible(true);
            setIcon.invoke(implementation, (Object)pixels);
        } catch (Throwable failure) { failed(failure); }
    }

    private static synchronized List<Icon> icons() throws Exception {
        if (icons != null) return icons;
        List<Icon> loaded = new ArrayList<Icon>();
        String path = System.getProperty("lanminecraft.window.icons", "");
        if (path.length() == 0) throw new IllegalStateException("Missing prepared window icons");
        try (ZipFile archive = new ZipFile(path)) {
            for (String name : new String[] { "icon_16x16.png", "icon_32x32.png", "icon_48x48.png", "icon_128x128.png", "icon_256x256.png" }) {
                ZipEntry entry = archive.getEntry(name);
                if (entry == null) entry = archive.getEntry("icons/" + name);
                if (entry == null) entry = archive.getEntry("assets/minecraft/icons/" + name);
                if (entry == null) continue;
                if (entry.getSize() <= 0 || entry.getSize() > 1024 * 1024) throw new IllegalStateException("Invalid window icon size");
                ImageReader reader = ImageIO.getImageReadersByFormatName("png").next();
                try (InputStream input = archive.getInputStream(entry);
                     MemoryCacheImageInputStream image = new MemoryCacheImageInputStream(input)) {
                    reader.setInput(image, true, true);
                    int width = reader.getWidth(0), height = reader.getHeight(0);
                    if (width <= 0 || height <= 0 || width > 256 || height > 256)
                        throw new IllegalStateException("Invalid window icon dimensions");
                    BufferedImage decoded = reader.read(0);
                    ByteBuffer rgba = ByteBuffer.allocateDirect(width * height * 4);
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
                        int pixel = decoded.getRGB(x, y);
                        rgba.put((byte)(pixel >>> 16)).put((byte)(pixel >>> 8)).put((byte)pixel).put((byte)(pixel >>> 24));
                    }
                    rgba.flip();
                    loaded.add(new Icon(width, height, rgba));
                } finally { reader.dispose(); }
            }
        }
        if (loaded.isEmpty()) throw new IllegalStateException("No prepared window icons");
        icons = loaded;
        return loaded;
    }

    private static synchronized void failed(Throwable failure) {
        if (failureReported) return;
        failureReported = true;
        System.err.println("[LauncherWindow] Initial window presentation failed:");
        failure.printStackTrace(System.err);
    }

    private static final class Icon {
        final int width, height;
        final ByteBuffer pixels;
        Icon(int width, int height, ByteBuffer pixels) { this.width = width; this.height = height; this.pixels = pixels; }
    }

    private static final class Modern {
        final Class<?> glfw;
        final ByteBuffer title;
        boolean visible = true, focused = true, pendingVisible, pendingFocused, pending, applying;
        Object imageBuffer;
        Class<?> bufferType;
        Modern(Class<?> glfw) throws Exception {
            this.glfw = glfw;
            byte[] encoded = PortableNoticeWindowHooks.title("Minecraft").getBytes("UTF-8");
            title = ByteBuffer.allocateDirect(encoded.length + 1);
            title.put(encoded).put((byte)0).flip();
        }
        void windowHint(int value) throws Exception {
            applying = true;
            try { glfw.getMethod("glfwWindowHint", int.class, int.class).invoke(null, VISIBLE, value); }
            finally { applying = false; }
        }
        void showWindow(long window) throws Exception {
            Integer previous = null;
            try {
                previous = ((Number)glfw.getMethod("glfwGetWindowAttrib", long.class, int.class)
                    .invoke(null, window, FOCUS_ON_SHOW)).intValue();
                glfw.getMethod("glfwSetWindowAttrib", long.class, int.class, int.class)
                    .invoke(null, window, FOCUS_ON_SHOW, pendingFocused ? 1 : 0);
            } catch (Throwable failure) { failed(failure); }
            try { glfw.getMethod("glfwShowWindow", long.class).invoke(null, window); }
            finally {
                if (previous != null) glfw.getMethod("glfwSetWindowAttrib", long.class, int.class, int.class)
                    .invoke(null, window, FOCUS_ON_SHOW, previous.intValue());
            }
        }
        Object images() throws Exception {
            if (imageBuffer == null) {
                List<Icon> source = icons();
                Class<?> image = Class.forName("org.lwjgl.glfw.GLFWImage", true, glfw.getClassLoader());
                bufferType = Class.forName("org.lwjgl.glfw.GLFWImage$Buffer", true, glfw.getClassLoader());
                imageBuffer = image.getMethod("create", int.class).invoke(null, source.size());
                for (int i = 0; i < source.size(); i++) {
                    Icon icon = source.get(i);
                    bufferType.getMethod("position", int.class).invoke(imageBuffer, i);
                    bufferType.getMethod("width", int.class).invoke(imageBuffer, icon.width);
                    bufferType.getMethod("height", int.class).invoke(imageBuffer, icon.height);
                    bufferType.getMethod("pixels", ByteBuffer.class).invoke(imageBuffer, icon.pixels);
                }
            }
            bufferType.getMethod("position", int.class).invoke(imageBuffer, 0);
            return imageBuffer;
        }
        void applyIcon(long window) throws Exception {
            images();
            glfw.getMethod("glfwSetWindowIcon", long.class, bufferType).invoke(null, window, imageBuffer);
        }
    }
}
