package com.codedisaster.steamworks;
public final class SteamID extends SteamNativeHandle {
    private SteamID(long value){super(value);}
    public static SteamID createFromNativeHandle(long value){return new SteamID(value);}
}
