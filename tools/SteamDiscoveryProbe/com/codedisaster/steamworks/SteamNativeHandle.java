package com.codedisaster.steamworks;
public class SteamNativeHandle {
    protected final long handle;
    protected SteamNativeHandle(long handle){this.handle=handle;}
    public static long getNativeHandle(SteamNativeHandle value){return value.handle;}
}
