import java.util.*;
import java.net.InetAddress;
/** Native transient LAN-list shape used by the real list transformer. */
public class SteamDiscoveryFakeLanList {
    public final List<Object> servers=new ArrayList<>();
    public boolean isDirty;
    public void addServer(String motd,InetAddress address) {
        synchronized(this) { servers.add(new SteamDiscoveryBehaviorProbe.LanServer(motd,address.getHostAddress()+":49441"));isDirty=true; }
    }
    public void addServer(String motd,String address) {
        synchronized(this) { servers.add(new SteamDiscoveryBehaviorProbe.LanServer(motd,address));isDirty=true; }
    }
}
