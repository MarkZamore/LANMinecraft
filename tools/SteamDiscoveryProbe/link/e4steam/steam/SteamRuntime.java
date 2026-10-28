package link.e4steam.steam;
import java.util.concurrent.*;
import com.codedisaster.steamworks.*;
public final class SteamRuntime {
    private static final SteamRuntime INSTANCE=new SteamRuntime();
    private static final ExecutorService WORKER=Executors.newSingleThreadExecutor(task->{Thread thread=new Thread(task,"fake-steam-worker");thread.setDaemon(true);return thread;});
    public static volatile boolean visible=true,nativeThreadCorrect=true;
    public static volatile boolean failNext,throwSubmission;
    public static volatile int failures;
    public static volatile int joins;
    public static volatile long joinedLobby,joinedFriend;
    private final SteamLobbyManager lobbyManager=new SteamLobbyManager();
    public static SteamRuntime get(){return INSTANCE;}
    public void awaitReady(){}
    private <T> CompletableFuture<T> submitSteamTask(Callable<T> task){
        if(throwSubmission)throw new IllegalStateException("Simulated stopped worker");
        return CompletableFuture.supplyAsync(()->{try{
            if(failNext){failNext=false;failures++;throw new java.io.IOException("Simulated transient failure");}
            return task.call();
        }catch(Exception e){throw new CompletionException(e);}},WORKER);
    }
    public static void nativeCheck(){nativeThreadCorrect&=Thread.currentThread().getName().equals("fake-steam-worker");}
    public static void shutdown(){WORKER.shutdownNow();}
}
final class SteamProtocol { static final byte VERSION=4; }
final class SteamLobbyManager {
    private final SteamFriends friends=new SteamFriends();
    private final Matchmaking matchmaking=new Matchmaking();
    private boolean requestJoin(SteamID lobby,SteamID friend,boolean directory){SteamRuntime.nativeCheck();SteamRuntime.joinedLobby=SteamNativeHandle.getNativeHandle(lobby);SteamRuntime.joinedFriend=SteamNativeHandle.getNativeHandle(friend);SteamRuntime.joins++;return true;}
}
final class Matchmaking {
    public boolean requestLobbyData(SteamID lobby){SteamRuntime.nativeCheck();return true;}
    public String getLobbyData(SteamID lobby,String key){long id=SteamNativeHandle.getNativeHandle(lobby);return key.equals("e4steam_protocol")?"4":id==110?"1.21.1":"1.20.1";}
    public SteamID getLobbyOwner(SteamID lobby){long id=SteamNativeHandle.getNativeHandle(lobby);return SteamID.createFromNativeHandle(id==120?99:id/10);}
}
