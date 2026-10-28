package com.codedisaster.steamworks;
import link.e4steam.steam.SteamRuntime;
public final class SteamFriends {
    public enum FriendFlags { Immediate }
    public int getFriendCount(FriendFlags flags){SteamRuntime.nativeCheck();return 3;}
    public SteamID getFriendByIndex(int index,FriendFlags flags){return SteamID.createFromNativeHandle(10+index);}
    public void requestFriendRichPresence(SteamID friend){SteamRuntime.nativeCheck();}
    public String getFriendRichPresence(SteamID friend,String key){return SteamRuntime.visible?"e4steam-lobby:"+(friend.handle*10):"";}
    public String getFriendPersonaName(SteamID friend){return "§Friend\n";}
}
