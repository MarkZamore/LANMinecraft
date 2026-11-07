package org.lwjgl.glfw;
import java.nio.ByteBuffer;
public final class GLFWImage {
    public static Buffer create(int count) { return new Buffer(count); }
    public static final class Buffer {
        public int count, index;
        public int[] widths, heights;
        public ByteBuffer[] pixels;
        Buffer(int n) { count=n; widths=new int[n]; heights=new int[n]; pixels=new ByteBuffer[n]; }
        public Buffer position(int i) { index=i; return this; }
        public Buffer width(int value) { widths[index]=value; return this; }
        public Buffer height(int value) { heights[index]=value; return this; }
        public Buffer pixels(ByteBuffer value) { pixels[index]=value; return this; }
    }
}
