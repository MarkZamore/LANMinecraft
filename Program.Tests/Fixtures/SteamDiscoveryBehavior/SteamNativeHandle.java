package com.codedisaster.steamworks;
public class SteamNativeHandle {
    protected final long value;
    protected SteamNativeHandle(long value) { this.value=value; }
    public static long getNativeHandle(SteamNativeHandle handle) { return handle.value; }
}
