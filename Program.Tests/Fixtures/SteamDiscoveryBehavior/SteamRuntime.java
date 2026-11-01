package link.e4steam.steam;

import java.util.*;
import java.util.concurrent.*;
import com.codedisaster.steamworks.*;

/** In-memory exact transport API fixture: no Steam library, network or real peer. */
public final class SteamRuntime implements AutoCloseable {
    public static SteamRuntime INSTANCE;
    public final LobbyManager lobbyManager=new LobbyManager();
    public volatile HostRegistration hostRegistration;
    public volatile Runnable beforeSteamTask;
    private final ExecutorService worker=Executors.newSingleThreadExecutor(task->{var thread=new Thread(task,"fake-steam-worker");thread.setDaemon(true);return thread;});
    public static SteamRuntime get() { return INSTANCE; }
    public void awaitReady() { }
    public CompletableFuture<Object> submitSteamTask(Callable<Object> task) {
        return CompletableFuture.supplyAsync(()->{try{var before=beforeSteamTask;if(before!=null)before.run();return task.call();}catch(Exception failure){throw new CompletionException(failure);}},worker);
    }
    public void close() { worker.shutdownNow(); }
    public record HostRegistration(Object owner,int localPort) { }
    public static final class LobbyManager {
        public final Friends friends=new Friends();
        public final Matchmaking matchmaking=new Matchmaking();
        public volatile Object hostLobbyOwner;
        public volatile long hostLobbyId;
        public final List<Long> requestedJoins=new CopyOnWriteArrayList<>();
        public void requestJoin(SteamID lobby,SteamID friend,boolean directory) { requestedJoins.add(SteamNativeHandle.getNativeHandle(lobby)); }
    }
    public static final class Friends {
        public final List<Long> ids=new CopyOnWriteArrayList<>();
        public final Map<Long,String> connects=new ConcurrentHashMap<>();
        public int getFriendCount(SteamFriends.FriendFlags flags) { return ids.size(); }
        public SteamID getFriendByIndex(int index,SteamFriends.FriendFlags flags) { return SteamID.createFromNativeHandle(ids.get(index)); }
        public void requestFriendRichPresence(SteamID friend) { }
        public String getFriendRichPresence(SteamID friend,String key) { return connects.get(SteamNativeHandle.getNativeHandle(friend)); }
        public String getFriendPersonaName(SteamID friend) { throw new AssertionError("Steam persona Shizoid must not replace launcher player Mitochka"); }
    }
    public static final class Matchmaking {
        public final Map<Long,Map<String,String>> data=new ConcurrentHashMap<>();
        public final Map<Long,Long> owners=new ConcurrentHashMap<>();
        public final List<String> writes=new CopyOnWriteArrayList<>();
        public void requestLobbyData(SteamID lobby) { }
        public String getLobbyData(SteamID lobby,String key) { return data.getOrDefault(SteamNativeHandle.getNativeHandle(lobby),Map.of()).getOrDefault(key,""); }
        public SteamID getLobbyOwner(SteamID lobby) { return SteamID.createFromNativeHandle(owners.getOrDefault(SteamNativeHandle.getNativeHandle(lobby),0L)); }
        public boolean setLobbyData(SteamID lobby,String key,String value) {
            long id=SteamNativeHandle.getNativeHandle(lobby);
            data.computeIfAbsent(id,unused->new ConcurrentHashMap<>()).put(key,value);writes.add(id+":"+key+"="+value);return true;
        }
    }
}
