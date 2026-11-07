import com.sun.jna.Native;
import com.sun.jna.Pointer;
import com.sun.jna.win32.StdCallLibrary;

final class WindowReceipt {
    interface User32 extends StdCallLibrary {
        int GetWindowTextW(Pointer window, char[] text, int length);
        Pointer SendMessageW(Pointer window, int message, Pointer wparam, Pointer lparam);
        boolean IsWindowVisible(Pointer window);
    }
    static final User32 WIN = Native.load("user32", User32.class);
    static void check(long handle) {
        Pointer window = new Pointer(handle);
        char[] text = new char[1024];
        WIN.GetWindowTextW(window, text, text.length);
        String actual = Native.toString(text);
        String expected = System.getProperty("lanminecraft.window.title");
        if (!expected.equals(actual)) throw new AssertionError("Initial native title: " + actual);
        long small = Pointer.nativeValue(WIN.SendMessageW(window, 0x007f, new Pointer(0), Pointer.NULL));
        long big = Pointer.nativeValue(WIN.SendMessageW(window, 0x007f, new Pointer(1), Pointer.NULL));
        if (small == 0 || big == 0) throw new AssertionError("Initial taskbar/caption icons are missing");
        if (!WIN.IsWindowVisible(window)) throw new AssertionError("Requested visible window stayed hidden");
        System.out.println("NATIVE_WINDOW_PASS|title=" + actual + "|hwnd=" + handle + "|small=" + small + "|big=" + big);
    }
}
