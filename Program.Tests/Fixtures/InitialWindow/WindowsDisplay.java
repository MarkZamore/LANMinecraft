package org.lwjgl.opengl;
import java.nio.ByteBuffer;
public final class WindowsDisplay {
    public static String title;
    public static boolean icon, shown;
    public void setTitle(String text) { title=text; }
    public int setIcon(ByteBuffer[] icons) {
        if((shown && !icon) || icons.length==0) throw new AssertionError("Legacy icon arrived too late");
        icon=true; return icons.length;
    }
    public void createWindow() { showWindow(1L,10); setTitle("Minecraft 1.6.4"); }
    private static void showWindow(long hwnd,int mode) {
        if(!icon || !System.getProperty("lanminecraft.window.title").equals(title))
            throw new AssertionError("Legacy first ShowWindow had generic presentation");
        shown=true;
    }
}
