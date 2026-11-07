import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.BooleanSupplier;
import java.lang.reflect.*;
import java.net.InetAddress;
import minecraft.portable.identity.PortableSteamDiscoveryHooks;
import minecraft.portable.identity.PortableSteamDiscoveryTransformer;
import link.e4steam.steam.SteamRuntime;

/** Executes actual discovery hooks with exact reflected fake transport API and no network. */
public final class SteamDiscoveryBehaviorProbe {
    public static final class Screen { public Object lanServerList;Screen(Object list){lanServerList=list;} }
    public static final class LanServer {
        public final String motd,address;
        public LanServer(String motd,String address){this.motd=motd;this.address=address;}
        public String getAddress(){return address;}
    }
    public static final class ServerData { public final String ip;ServerData(String ip){this.ip=ip;} }
    public static final class Component {
        public final String text;public Component(String text){this.text=text;}
        public static Component literal(String text){return new Component(text);}
    }
    public static final class User { public String getName(){return "Mitochka";} }
    public static final class WorldData { public String getLevelName(){return "Новый мир, созданный в игре";} }
    public static final class Server implements Executor {
        public WorldData getWorldData(){return new WorldData();}
        public void execute(Runnable task){task.run();}
    }
    public static final class Minecraft implements Executor {
        public static final Minecraft INSTANCE=new Minecraft();
        public volatile Server current;
        public static Minecraft getInstance(){return INSTANCE;}
        public Server getSingleplayerServer(){return current;}
        public User getUser(){return new User();}
        public void execute(Runnable task){task.run();}
    }
    private static final String PREFIX="minecraft.portable.identity.";
    private static void check(boolean value,String reason){if(!value)throw new AssertionError(reason);}
    private static void waitFor(BooleanSupplier condition,String reason)throws Exception{
        long end=System.nanoTime()+TimeUnit.SECONDS.toNanos(5);
        while(!condition.getAsBoolean()){if(System.nanoTime()>=end)throw new AssertionError(reason);Thread.sleep(5);}
    }
    @SuppressWarnings("unchecked") private static List<Object> servers(Object list)throws Exception{return (List<Object>)list.getClass().getField("servers").get(list);}
    private static void configure(){
        Map.ofEntries(Map.entry("steamDiscoveryEnabled","true"),Map.entry("steamDiscoveryTransportVersion","0.3.4"),
            Map.entry("steamDiscoveryMinecraftVersion","1.20.1"),Map.entry("minecraftClasses",Minecraft.class.getName()),
            Map.entry("minecraftGetInstanceMethods","getInstance"),Map.entry("steamDiscoveryLanListFields","lanServerList"),
            Map.entry("steamDiscoveryLanServersFields","servers"),Map.entry("steamDiscoveryLanDirtyFields","isDirty"),
            Map.entry("steamDiscoveryLanServerClasses",LanServer.class.getName()),Map.entry("steamDiscoveryLanAddressMethods","getAddress"),
            Map.entry("steamDiscoveryServerAddressFields","ip"),Map.entry("steamDiscoveryLanListClasses","SteamDiscoveryFakeLanList"),
            Map.entry("steamDiscoveryLanAddMethods","addServer"),Map.entry("steamDiscoveryLanAddDescriptors","(Ljava/lang/String;Ljava/net/InetAddress;)V"),
            Map.entry("steamDiscoveryComponentClasses",Component.class.getName()),
            Map.entry("steamDiscoveryComponentLiteralMethods","literal"),Map.entry("getSingleplayerServerMethods","getSingleplayerServer"),
            Map.entry("steamDiscoveryGetUserMethods","getUser"),Map.entry("steamDiscoveryUserNameMethods","getName"),
            Map.entry("getWorldDataMethods","getWorldData"),Map.entry("steamDiscoveryLevelNameMethods","getLevelName"))
            .forEach((name,value)->System.setProperty(PREFIX+name,value));
    }
    private static Object transformedList()throws Exception{return transformedList(true);}
    private static Object transformedList(boolean inetAddress)throws Exception{
        System.setProperty(PREFIX+"steamDiscoveryLanAddDescriptors",inetAddress?"(Ljava/lang/String;Ljava/net/InetAddress;)V":"(Ljava/lang/String;Ljava/lang/String;)V");
        byte[] original;try(var input=SteamDiscoveryBehaviorProbe.class.getResourceAsStream("/SteamDiscoveryFakeLanList.class")){original=input.readAllBytes();}
        byte[] changed=new PortableSteamDiscoveryTransformer().transform(null,null,"SteamDiscoveryFakeLanList",null,null,original);
        check(changed!=null,"Native UDP list guard was not installed");
        Class<?> type=new ClassLoader(SteamDiscoveryBehaviorProbe.class.getClassLoader()){
            Class<?> define(){return defineClass("SteamDiscoveryFakeLanList",changed,0,changed.length);}
        }.define();
        return type.getConstructor().newInstance();
    }
    private static void add(Object list,String motd,String address)throws Exception{
        String[] components=address.split(":",2)[0].split("\\.");
        byte[] octets=new byte[4];for(int i=0;i<octets.length;i++)octets[i]=(byte)Integer.parseInt(components[i]);
        list.getClass().getMethod("addServer",String.class,InetAddress.class).invoke(list,motd,InetAddress.getByAddress(octets));
    }
    private static void lobby(SteamRuntime runtime,long friend,long lobby,long owner,String protocol,boolean description){
        runtime.lobbyManager.friends.ids.add(friend);
        runtime.lobbyManager.friends.connects.put(friend,"e4steam-lobby:"+lobby);
        runtime.lobbyManager.matchmaking.owners.put(lobby,owner);
        var data=new ConcurrentHashMap<String,String>();data.put("e4steam_protocol",protocol);data.put("e4steam_minecraft","1.20.1");
        if(description){data.put("lanminecraft_display","1");data.put("lanminecraft_player","Mitochka");data.put("lanminecraft_world","Новый мир");}
        runtime.lobbyManager.matchmaking.data.put(lobby,data);
    }
    public static void main(String[] args)throws Exception{
        configure();
        try(var runtime=new SteamRuntime()){
            SteamRuntime.INSTANCE=runtime;
            lobby(runtime,101,201,101,"3",true);
            lobby(runtime,102,202,999,"3",true); // Wrong owner.
            lobby(runtime,103,203,103,"wrong",true); // Wrong wire protocol.
            lobby(runtime,104,204,104,"3",false); // No trusted metadata.
            Object list=transformedList(),unrelated=transformedList();
            add(list,"Mitochka - Новый мир","192.168.0.110:49441");
            add(unrelated,"Manual LAN fixture","192.168.0.111:49441");
            check(servers(list).isEmpty() && servers(unrelated).isEmpty(), "UDP packets before screen initialization must be suppressed");
            var manual=new ArrayList<>(List.of(new ServerData("saved.example:25565")));
            Screen screen=new Screen(list);
            try{
                PortableSteamDiscoveryHooks.begin(screen);
                check(PortableSteamDiscoveryHooks.nativeDiscoveryManaged(list),"Bound list not marked managed");
                check(PortableSteamDiscoveryHooks.nativeDiscoveryManaged(unrelated),"Steam-only UDP policy must cover every automatic list");
                Object initialList=list;
                waitFor(()->{try{synchronized(initialList){return servers(initialList).size()==1&&((LanServer)servers(initialList).get(0)).address.startsWith("portable-steam-");}}catch(Exception e){throw new AssertionError(e);}},"One trusted Steam world did not replace UDP duplicates");
                LanServer found;synchronized(list){check(servers(list).size()==1,"Wrong owner/protocol/missing metadata was listed");found=(LanServer)servers(list).get(0);}
                check(found.address.equals("portable-steam-201.invalid"),"Lobby identity changed");
                check(found.motd.equals("Mitochka"),"Steam persona replaced launcher player name");
                Component original=new Component("LAN World");
                check(((Component)PortableSteamDiscoveryHooks.worldTitle(original,found)).text.equals("Новый мир"),"Native header did not use actual world metadata");
                check(PortableSteamDiscoveryHooks.worldTitle(original,new LanServer("ordinary","192.168.0.110:49441"))==original,"Native row label changed");
                check(PortableSteamDiscoveryHooks.worldTitle(original,new LanServer("unregistered",found.address))==original,"Unregistered equal-address object trusted as a managed row");
                check(PortableSteamDiscoveryHooks.displayAddress(found.getAddress(),found).equals("Steam"),"Synthetic routing ID leaked into the visible transport label");
                check(found.getAddress().equals("portable-steam-201.invalid"),"Visible transport label changed the real connection address");
                LanServer ordinary=new LanServer("native","192.168.0.110:49441"),unregistered=new LanServer("unregistered",found.address);
                check(PortableSteamDiscoveryHooks.displayAddress(ordinary.getAddress(),ordinary).equals(ordinary.address),"Native address display changed");
                check(PortableSteamDiscoveryHooks.displayAddress(unregistered.getAddress(),unregistered).equals(unregistered.address),"Unregistered equal-address object received Steam display trust");
                add(list,"duplicate UDP","192.168.0.110:49441");
                synchronized(list){check(servers(list).size()==1,"Native UDP reappeared in a managed Steam list");}
                check(servers(unrelated).isEmpty()&&manual.size()==1,"Automatic UDP is suppressed while manual/saved servers remain");
                check(PortableSteamDiscoveryHooks.join(screen,new ServerData("portable-steam-999.invalid")),"Stale synthetic address reached DNS");
                check(!PortableSteamDiscoveryHooks.join(screen,new ServerData("saved.example:25565")),"Ordinary saved server was intercepted");
                check(PortableSteamDiscoveryHooks.join(screen,new ServerData(found.address)),"Known Steam world was not consumed");
                waitFor(()->runtime.lobbyManager.requestedJoins.contains(201L),"Author native requestJoin was not called");
                Object previous=list,replacement=transformedList();
                add(replacement,"pre-resize UDP","192.168.0.110:49441");
                screen.lanServerList=replacement;
                PortableSteamDiscoveryHooks.begin(screen);
                check(PortableSteamDiscoveryHooks.nativeDiscoveryManaged(previous),"Resize must retain UDP suppression for the old detector");
                check(PortableSteamDiscoveryHooks.nativeDiscoveryManaged(replacement),"Resize did not bind the new native list");
                synchronized(replacement){check(servers(replacement).size()==1&&((LanServer)servers(replacement).get(0)).motd.equals("Mitochka"),"Resize lost the current Steam world or retained UDP");}
                add(previous,"ordinary old list","192.168.0.110:49441");
                synchronized(previous){check(servers(previous).size()==1,"Unbound pre-resize detector reintroduced native UDP");}
                add(replacement,"duplicate resized UDP","192.168.0.110:49441");
                synchronized(replacement){check(servers(replacement).size()==1,"Resized managed list reintroduced UDP");}
                list=replacement;
            }finally{PortableSteamDiscoveryHooks.end(screen);}
            check(PortableSteamDiscoveryHooks.nativeDiscoveryManaged(list),"Removed screen must retain Steam-only UDP policy");
            add(list,"ordinary after end","192.168.0.110:49441");
            synchronized(list){check(servers(list).size()==1,"Late UDP packet appeared after managed screen ended");}
            System.setProperty(PREFIX+"steamDiscoveryTransportVersion","unreviewed");
            Screen unsupported=new Screen(unrelated);
            try{
                PortableSteamDiscoveryHooks.begin(unsupported);
                check(PortableSteamDiscoveryHooks.nativeDiscoveryManaged(unrelated),"Unavailable Steam transport must not re-enable UDP duplicates");
                check(servers(unrelated).isEmpty(),"Unsupported transport exposed a native LAN row");
            }finally{
                PortableSteamDiscoveryHooks.end(unsupported);
                System.setProperty(PREFIX+"steamDiscoveryTransportVersion","0.3.4");
            }
            publication(runtime);
            legacyDescriptor(runtime);
            System.setProperty(PREFIX+"steamDiscoveryEnabled", "false");
            check(!PortableSteamDiscoveryHooks.nativeDiscoveryManaged(unrelated), "Disabled integration preserves native UDP");
            add(unrelated,"ordinary with integration disabled","192.168.0.111:49441");
            check(servers(unrelated).size()==1 && manual.size()==1, "Disabled integration and saved servers retain native behavior");
        }
        System.out.println("PASS: actual discovery hooks list one Steam world with launcher nickname/live world metadata, scope the UDP guard, preserve manual servers and route joins; publication uses live owner generation. No game, Steam or socket started.");
    }
    private static void legacyDescriptor(SteamRuntime runtime)throws Exception{
        Object list=transformedList(false);Screen screen=new Screen(list);
        var add=list.getClass().getMethod("addServer",String.class,String.class);
        add.invoke(list,"legacy before management","192.168.0.110:49441");
        try{
            PortableSteamDiscoveryHooks.begin(screen);
            check(PortableSteamDiscoveryHooks.nativeDiscoveryManaged(list),"Legacy descriptor fixture did not bind");
            waitFor(()->{try{synchronized(list){return servers(list).size()==1;}}catch(Exception failure){throw new AssertionError(failure);}},"Legacy descriptor fixture did not list the Steam world");
            int before;synchronized(list){before=servers(list).size();}
            add.invoke(list,"legacy duplicate UDP","192.168.0.110:49441");
            synchronized(list){check(servers(list).size()==before,"Selected String/String descriptor did not guard native additions");}
        }finally{
            PortableSteamDiscoveryHooks.end(screen);
            System.setProperty(PREFIX+"steamDiscoveryLanAddDescriptors","(Ljava/lang/String;Ljava/net/InetAddress;)V");
        }
    }
    private static void publication(SteamRuntime runtime)throws Exception{
        var client=Minecraft.INSTANCE;var server=new Server();client.current=server;
        Object owner=new Object();runtime.hostRegistration=new SteamRuntime.HostRegistration(owner,49441);
        runtime.lobbyManager.hostLobbyOwner=owner;runtime.lobbyManager.hostLobbyId=301;
        PortableSteamDiscoveryHooks.published(client,server,49441);
        waitFor(()->runtime.lobbyManager.matchmaking.writes.size()==3,"Live publication metadata was not written");
        var values=runtime.lobbyManager.matchmaking.data.get(301L);
        check(values.get("lanminecraft_player").equals("Mitochka"),"Published player did not come from live User.getName");
        check(values.get("lanminecraft_world").equals("Новый мир, созданный в игре"),"Published world came from a stale startup folder/MOTD");
        check(runtime.lobbyManager.matchmaking.writes.stream().allMatch(value->value.contains(":lanminecraft_")),"Core e4steam protocol/endpoint metadata modified");
        PortableSteamDiscoveryHooks.published(client,server,49441);Thread.sleep(30);
        check(runtime.lobbyManager.matchmaking.writes.size()==3,"Same server published metadata twice");
        var stale=new Server();client.current=new Server();
        PortableSteamDiscoveryHooks.published(client,stale,49441);Thread.sleep(60);
        check(runtime.lobbyManager.matchmaking.writes.size()==3,"Stale server wrote metadata into a replacement world");
        var changing=new Server();client.current=changing;
        Object previous=new Object();runtime.hostRegistration=new SteamRuntime.HostRegistration(previous,49441);
        runtime.lobbyManager.hostLobbyOwner=previous;runtime.lobbyManager.hostLobbyId=401;
        var tasks=new AtomicInteger();
        runtime.beforeSteamTask=()->{
            if(tasks.incrementAndGet()==2){
                Object replacement=new Object();runtime.hostRegistration=new SteamRuntime.HostRegistration(replacement,49441);
                runtime.lobbyManager.hostLobbyOwner=replacement;runtime.lobbyManager.hostLobbyId=402;
                client.current=new Server();
            }
        };
        try{
            PortableSteamDiscoveryHooks.published(client,changing,49441);
            waitFor(()->tasks.get()>=3,"Owner generation race did not reach the next current-world check");
            check(runtime.lobbyManager.matchmaking.writes.size()==3,"Old server wrote into a replacement hosting generation");
        }finally{runtime.beforeSteamTask=null;}
    }
}
