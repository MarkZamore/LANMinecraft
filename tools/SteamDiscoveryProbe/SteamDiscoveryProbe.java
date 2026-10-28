package minecraft.portable.identity;

import java.io.*;
import java.lang.reflect.*;
import java.util.*;
import probe.*;
import link.e4steam.steam.SteamRuntime;

/** Deterministic native-service fakes; never claims a two-account Steam result. */
public final class SteamDiscoveryProbe {
    private static void check(boolean value, String why) { if (!value) throw new AssertionError(why); }
    private static void await(java.util.function.BooleanSupplier condition) throws Exception {
        long deadline = System.nanoTime() + 10_000_000_000L;
        while (!condition.getAsBoolean() && System.nanoTime() < deadline) { Minecraft.flush(); Thread.sleep(25); }
        Minecraft.flush();
        check(condition.getAsBoolean(), "Timed out awaiting discovery state");
    }
    public static void main(String[] args) throws Exception {
        String transportVersion = args.length == 0 ? "0.3.2" : args[0];
        Map<String,String> properties = Map.ofEntries(
            Map.entry("steamDiscoveryEnabled", "true"),
            Map.entry("steamDiscoveryScreenClasses", "probe/Screen"),
            Map.entry("steamDiscoveryInitMethods", "init"),
            Map.entry("steamDiscoveryRemovedMethods", "removed"),
            Map.entry("steamDiscoveryJoinMethods", "join"),
            Map.entry("steamDiscoveryJoinDescriptors", "(Lprobe/Data;)V"),
            Map.entry("steamDiscoveryServerAddressFields", "ip"),
            Map.entry("steamDiscoveryLanListFields", "lanList"),
            Map.entry("steamDiscoveryLanServersFields", "servers"),
            Map.entry("steamDiscoveryLanDirtyFields", "dirty"),
            Map.entry("steamDiscoveryLanServerClasses", "probe.Entry"),
            Map.entry("steamDiscoveryLanAddressMethods", "getAddress"),
            Map.entry("steamDiscoveryMinecraftVersion", "1.20.1"),
            Map.entry("steamDiscoveryTransportVersion", transportVersion),
            Map.entry("minecraftClasses", "probe.Minecraft"),
            Map.entry("minecraftGetInstanceMethods", "getInstance"));
        properties.forEach((name,value) -> System.setProperty("minecraft.portable.identity."+name,value));
        byte[] source;
        try (InputStream input = SteamDiscoveryProbe.class.getResourceAsStream("/probe/Screen.class")) { source=input.readAllBytes(); }
        byte[] changed = new PortableSteamDiscoveryTransformer().transform(null,null,"probe/Screen",null,null,source);
        ClassLoader loader = new ClassLoader(SteamDiscoveryProbe.class.getClassLoader()) {
            @Override protected Class<?> loadClass(String name, boolean resolve) throws ClassNotFoundException {
                if (!name.equals("probe.Screen")) return super.loadClass(name,resolve);
                Class<?> found=findLoadedClass(name); if(found==null) found=defineClass(name,changed,0,changed.length);
                if(resolve)resolveClass(found); return found;
            }
        };
        Class<?> type=loader.loadClass("probe.Screen"); Object screen=type.getConstructor().newInstance();
        LanList list=(LanList)type.getField("lanList").get(screen);
        list.servers.add(new Entry("Local world","192.0.2.1:25565"));
        System.setProperty("minecraft.portable.identity.steamDiscoveryTransportVersion","0.3.3");
        type.getMethod("init").invoke(screen); Minecraft.flush();
        check(list.servers.size()==1,"Unprofiled transport interface is left alone");
        System.setProperty("minecraft.portable.identity.steamDiscoveryTransportVersion",transportVersion);
        type.getMethod("init").invoke(screen);
        await(() -> list.servers.size()==2);
        check(list.dirty,"Vanilla list dirty flag must be set");
        check(list.servers.get(1).getMotd().equals("Steam · Friend"),"Sanitize persona text");
        check(list.servers.get(1).getAddress().equals("portable-steam-100.invalid"),"No private endpoint token in list");
        check(SteamRuntime.nativeThreadCorrect,"Steam native calls stay on its own worker");
        type.getMethod("init").invoke(screen);
        type.getMethod("join",Data.class).invoke(screen,new Data(list.servers.get(1).getAddress()));
        await(() -> SteamRuntime.joins==1);
        check(SteamRuntime.joinedLobby==100 && SteamRuntime.joinedFriend==10,"Author join receives exact selected lobby and friend");
        check(type.getField("vanillaJoins").getInt(screen)==0,"Steam entry bypasses ordinary DNS/connect");
        type.getMethod("join",Data.class).invoke(screen,new Data("localhost:25565"));
        check(type.getField("vanillaJoins").getInt(screen)==1,"Ordinary server join remains vanilla");
        SteamRuntime.throwSubmission=true;
        type.getMethod("join",Data.class).invoke(screen,new Data(list.servers.get(1).getAddress()));
        check(type.getField("vanillaJoins").getInt(screen)==1,"Submission failure cannot send synthetic entry to DNS");
        SteamRuntime.throwSubmission=false;
        SteamRuntime.failNext=true;
        SteamRuntime.visible=false;
        await(() -> list.servers.size()==1);
        check(SteamRuntime.failures==1,"Transient worker failure was exercised");
        SteamRuntime.visible=true;
        await(() -> list.servers.size()==2);
        SteamRuntime.visible=false;
        await(() -> list.servers.size()==1);
        type.getMethod("join",Data.class).invoke(screen,new Data("portable-steam-100.invalid"));
        check(type.getField("vanillaJoins").getInt(screen)==1,"Stale synthetic entry never reaches DNS");
        type.getMethod("removed").invoke(screen); SteamRuntime.visible=true;
        Thread.sleep(3200); Minecraft.flush();
        check(list.servers.size()==1,"Closed screen cannot receive late discovery results");
        check(PortableSteamDiscoveryHooks.lobbyId("e4steam-lobby:18446744073709551616")==0,"Reject overflowing lobby");
        check(PortableSteamDiscoveryHooks.lobbyId("e4steam-lobby:0")==0,"Reject zero lobby");
        check(PortableSteamDiscoveryHooks.lobbyId("https://example.com")==0,"Reject unrelated presence");
        SteamRuntime.shutdown();
        System.out.println("PASS: verified transformed screen, worker confinement, compatibility/owner filtering, transient list, author join, vanilla join, resize, stale entry and cancellation. Native Steam/accounts not exercised.");
    }
}
