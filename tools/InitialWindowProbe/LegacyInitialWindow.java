import java.lang.reflect.Field;
import java.lang.reflect.Method;
import org.lwjgl.opengl.Display;
import org.lwjgl.opengl.DisplayMode;

public final class LegacyInitialWindow {
    public static void main(String[] args) throws Exception {
        Display.setDisplayMode(new DisplayMode(320, 200));
        Display.setTitle("Minecraft 1.6.4");
        Display.create();
        try {
            Field field = Display.class.getDeclaredField("display_impl"); field.setAccessible(true);
            Object implementation = field.get(null);
            Method handle = implementation.getClass().getDeclaredMethod("getHwnd"); handle.setAccessible(true);
            long hwnd = ((Number)handle.invoke(implementation)).longValue();
            WindowReceipt.check(hwnd);
            Display.setTitle("Minecraft 1.6.4 loader progress");
            Display.setIcon(new java.nio.ByteBuffer[0]);
            WindowReceipt.check(hwnd);
            Display.processMessages();
        } finally { Display.destroy(); }
    }
}
