import java.lang.reflect.Field;
import java.util.*;
import minecraft.portable.identity.PortablePerPlayerChunksHooks;

/** Execute the production pacing hold against a native-shaped player/channel chain. */
public final class ChunkBackpressureProbe {
    static final UUID LOCAL = UUID.fromString("00000000-0000-4000-8000-000000000001");
    static int checks;
    static void check(boolean value, String message) { checks++; if (!value) throw new AssertionError(message); }
    static void property(String key, String value) { System.setProperty("minecraft.portable.identity." + key, value); }
    public static void main(String[] args) throws Exception {
        String mode = args[0]; configure(mode);
        NativeMap map = new NativeMap();
        Player player = new Player(map, mode.equals("local") ? LOCAL : UUID.randomUUID());
        Channel channel = new Channel(); player.listener.connection.channel = channel;
        channel.writable = false;
        if (mode.equals("memory")) player.listener.connection.memory = true;
        if (mode.equals("missing-method")) player.listener.connection.channel = new Object();
        if (mode.equals("null-channel")) player.listener.connection.channel = null;
        if (mode.equals("throwing")) channel.fail = true;
        if (mode.equals("local")) {
            offer(map, player, 0);
            check(player.received.equals(List.of(0)), "Local integrated player was delayed by an unwritable unused wire");
            check(held(player) == 0, "Local integrated player entered the network hold");
        } else if (!mode.equals("normal")) {
            for (int x=0;x<6;x++) offer(map, player, x);
            PortablePerPlayerChunksHooks.deliverHeld(map);
            check(player.received.equals(List.of(0,1,2,3)), "Unavailable capability or memory connection changed existing four-chunk budget");
            PortablePerPlayerChunksHooks.deliverHeld(map);
            check(player.received.equals(List.of(0,1,2,3,4,5)), "Fallback lost held chunks");
            check(held(player) == 0, "Fallback failed to drain existing pacing hold");
        } else {
            // This first join has no answered keep-alive yet: latency remains
            // zero even while the actual channel has already stopped accepting.
            check(player.latency == 0, "Fixture no longer covers first keep-alive blind spot");
            for (int x=0;x<8;x++) offer(map, player, x);
            check(held(player) == 8, "Offers did not enter the real production hold");
            for (int tick=0;tick<3;tick++) PortablePerPlayerChunksHooks.deliverHeld(map);
            check(player.received.isEmpty(), "Unwritable channel still received chunks before the first keep-alive reply");
            check(held(player) == 8, "Pause consumed pending positions or chunk objects");
            PortablePerPlayerChunksHooks.cancelHeld(player, new Pos(3,0));
            channel.writable = true; channel.acceptsBeforeBlock = 1;
            PortablePerPlayerChunksHooks.deliverHeld(map);
            check(player.received.equals(List.of(0)), "Pump did not recheck writability between native handovers");
            check(held(player) == 7, "Mid-batch pause removed an unsent chunk");
            channel.writable = true; channel.acceptsBeforeBlock = Integer.MAX_VALUE;
            PortablePerPlayerChunksHooks.deliverHeld(map);
            check(player.received.equals(List.of(0,1,2,4,5)), "Resume broke FIFO, cancellation or existing four-chunk budget");
            PortablePerPlayerChunksHooks.deliverHeld(map);
            check(player.received.equals(List.of(0,1,2,4,5,6,7)), "Resume lost, duplicated or delivered a cancelled chunk");
            check(held(player) == 0, "Resume did not clear the hold");
            check(map.viewDistance == 64, "Backpressure changed native view distance");
            // A disconnected/replaced ServerPlayer must not receive the old
            // object's held work when the same UUID reconnects.
            channel.writable = false; offer(map,player,10);
            Player replacement = new Player(map,player.uuid);
            replacement.listener.connection.channel = new Channel();
            offer(map,replacement,11); PortablePerPlayerChunksHooks.deliverHeld(map);
            check(replacement.received.equals(List.of(11)), "Reconnect inherited a stale pending chunk from old player object");
            check(!player.received.contains(10), "Old player object received work after replacement");
        }
        System.out.println("CHUNK_BACKPRESSURE_PASSED mode="+mode+" checks="+checks+" nativeView="+map.viewDistance);
    }
    static void offer(NativeMap map,Player player,int x) {
        Chunk chunk = new Chunk(x,0); Scratch scratch = new Scratch();
        if (PortablePerPlayerChunksHooks.admit(map,player,scratch,chunk)) map.handChunk(player,scratch,chunk);
    }
    static int held(Player player) throws Exception {
        Field f=PortablePerPlayerChunksHooks.class.getDeclaredField("HELD");f.setAccessible(true);
        Object pending=((Map<?,?>)f.get(null)).get(player.uuid); if(pending==null)return 0;
        Field q=pending.getClass().getDeclaredField("queue");q.setAccessible(true);
        return ((Collection<?>)q.get(pending)).size();
    }
    static void configure(String mode) {
        property("chunkPacingEnabled","true"); property("chunkBackpressureEnabled",mode.equals("disabled")?"false":"true");
        property("localPlayer",LOCAL.toString()); property("chunkViewDistanceFields","viewDistance");
        property("playerUuidMethods","getUUID"); property("chunkGetPosMethods","getPos"); property("chunkPositionMethods","chunkPosition");
        property("chunkPosXFields","x");property("chunkPosZFields","z");property("playerLoadedChunkMethods","handChunk");
        property("serverLevelMethods","serverLevel");property("getChunkSourceMethods","getChunkSource");property("chunkMapFields","map");
        property("playerLatencyFields","latency");property("chunkPlayerListenerFields",mode.equals("missing-field")?"absent":"listener");
        property("chunkListenerConnectionFields","connection");property("chunkConnectionChannelFields","channel");
        property("chunkMemoryConnectionMethods","isMemoryConnection");
    }
    public static final class NativeMap {
        public int viewDistance=64;
        public void handChunk(Player player,Scratch scratch,Chunk chunk) {
            if(player.received.contains(chunk.position.x)) throw new AssertionError("Duplicate native delivery");
            player.received.add(chunk.position.x);
            if(player.listener.connection.channel instanceof Channel channel && --channel.acceptsBeforeBlock == 0) channel.writable=false;
        }
    }
    public static final class Player {
        public final UUID uuid;public final Listener listener=new Listener();public final Level level;public int latency;
        public final List<Integer> received=new ArrayList<>();
        Player(Object map,UUID uuid){this.uuid=uuid;level=new Level(map);}
        public UUID getUUID(){return uuid;}public Pos chunkPosition(){return new Pos(0,0);}public Level serverLevel(){return level;}
    }
    public static final class Listener { public final Connection connection=new Connection(); }
    public static final class Connection { public Object channel;public boolean memory;public boolean isMemoryConnection(){return memory;} }
    public static final class Channel {
        public boolean writable=true,fail;public int acceptsBeforeBlock=Integer.MAX_VALUE;
        public boolean isWritable(){if(fail)throw new IllegalStateException("Unavailable optional channel API");return writable;}
    }
    public static final class Level { public final Source source;Level(Object map){source=new Source(map);}public Source getChunkSource(){return source;} }
    public static final class Source { public final Object map;Source(Object map){this.map=map;} }
    public static final class Pos { public final int x,z;Pos(int x,int z){this.x=x;this.z=z;} }
    public static final class Chunk { public final Pos position;Chunk(int x,int z){position=new Pos(x,z);}public Pos getPos(){return position;} }
    public static final class Scratch { public Scratch(){} }
}
