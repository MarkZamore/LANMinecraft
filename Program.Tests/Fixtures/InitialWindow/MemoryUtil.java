package org.lwjgl.system;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Map;
public final class MemoryUtil {
    private static final Map<Long,String> TEXT = new HashMap<Long,String>();
    public static long memAddress(ByteBuffer buffer) {
        ByteBuffer read = buffer.duplicate();
        byte[] bytes = new byte[read.remaining()]; read.get(bytes);
        int length = 0; while (length < bytes.length && bytes[length] != 0) length++;
        long pointer = TEXT.size() + 1000;
        TEXT.put(pointer, new String(bytes, 0, length, StandardCharsets.UTF_8)); return pointer;
    }
    public static String text(long pointer) { return TEXT.containsKey(pointer) ? TEXT.get(pointer) : "Minecraft 26.3"; }
}
