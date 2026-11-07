package org.lwjgl.glfw;
import org.lwjgl.system.MemoryUtil;
public final class GLFW {
    public static boolean hintVisible=true, hintFocused=true, visible, focused, focusOnShow=true, icon, fail;
    public static int shows;
    public static String title;
    public static void glfwDefaultWindowHints() { hintVisible=true; hintFocused=true; }
    public static void glfwWindowHint(int key,int value) { if(key==0x20004) hintVisible=value!=0; if(key==0x20001) hintFocused=value!=0; }
    public static int glfwGetWindowAttrib(long window,int key) { return (key==0x2000c ? focusOnShow : focused) ? 1 : 0; }
    public static void glfwSetWindowAttrib(long window,int key,int value) { if(key==0x2000c) focusOnShow=value!=0; }
    public static long nglfwCreateWindow(int w,int h,long text,long monitor,long share) {
        if(fail) return 0;
        title=MemoryUtil.text(text); visible=hintVisible; focused=visible&&hintFocused; focusOnShow=true; icon=false; if(visible) shows++;
        return 7;
    }
    public static void nglfwSetWindowTitle(long window,long pointer) { title=MemoryUtil.text(pointer); }
    public static void glfwSetWindowIcon(long window,GLFWImage.Buffer source) {
        if(Boolean.getBoolean("fixture.window.iconFailure")) throw new IllegalStateException("Fixture icon failure");
        if(visible && !icon) throw new AssertionError("Icon arrived after the first visible frame");
        if(source.count==0) throw new AssertionError("No icons");
        for(int i=0;i<source.count;i++)
            if(source.pixels[i]==null || source.pixels[i].remaining()!=source.widths[i]*source.heights[i]*4)
                throw new AssertionError("Invalid RGBA pixels");
        icon=true;
    }
    public static void glfwShowWindow(long window) {
        if((!icon && !Boolean.getBoolean("fixture.window.iconFailure")) || !title.equals(System.getProperty("lanminecraft.window.title")))
            throw new AssertionError("Wrong presentation on first ShowWindow");
        visible=true; focused=focusOnShow; shows++;
    }
    public static void glfwSwapBuffers(long window) { }
}
