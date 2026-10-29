import java.io.InputStream;
import java.lang.reflect.Field;
import java.util.*;
import minecraft.portable.identity.PortablePerPlayerChunksHooks;
import minecraft.portable.identity.PortablePerPlayerChunksTransformer;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.*;

/** Real common transformer/hooks, fake native API; no Minecraft process or network. */
public final class ChunkPacingDistanceProbe {
    static final String TARGET = "ChunkPacingDistanceProbe$NativeMap";
    static int checks;
    static void check(boolean value, String message) { checks++; if (!value) throw new AssertionError(message); }
    static void property(String key, String value) { System.setProperty("minecraft.portable.identity." + key, value); }
    public static void main(String[] args) throws Exception {
        configure();
        byte[] original;
        try (InputStream in = ChunkPacingDistanceProbe.class.getResourceAsStream("/" + TARGET + ".class")) { original = in.readAllBytes(); }
        byte[] changed = new PortablePerPlayerChunksTransformer().transform(null, null, TARGET, null, null, original);
        check(changed != null, "Native field consumer was not transformed");
        var node = new ClassNode(); new ClassReader(changed).accept(node, 0);
        int nativeReads = 0, passThroughCalls = 0, unrelatedCalls = 0;
        for (MethodNode method : node.methods) for (AbstractInsnNode instruction : method.instructions) {
            if (method.name.equals("distanceFor") && instruction instanceof FieldInsnNode field && field.getOpcode() == Opcodes.GETFIELD && field.name.equals("viewDistance")) nativeReads++;
            if (instruction instanceof MethodInsnNode call && call.name.equals("radiusFor")) {
                if (method.name.equals("distanceFor") && call.desc.equals("(Ljava/lang/Object;ILjava/lang/Object;)I")) passThroughCalls++;
                if (method.name.equals("unrelatedDistance")) unrelatedCalls++;
            }
        }
        check(nativeReads == 1 && passThroughCalls == 1, "Original GETFIELD value was removed or helper signature changed");
        check(unrelatedCalls == 0, "Unrelated native distance read changed");
        // The bytecode has already matched its actual field. Make optional
        // reflection fail independently, proving the real operand is retained.
        if (args[0].equals("reflection-miss")) property("chunkViewDistanceFields", "missingNativeField");
        Class<?> type = new Loader().define(changed); Object map = type.getConstructor().newInstance();
        Player player = new Player(map);
        type.getMethod("setViewDistance", int.class).invoke(map, 64);
        check(distance(type, map, player) == 64, "Unknown choice did not preserve native author radius64");
        PortablePerPlayerChunksHooks.observeOptions(player, new Info(96));
        check(distance(type, map, player) == 64, "Requested96 injected a custom32 ceiling or escaped native64");
        check(PortablePerPlayerChunksHooks.largestAsked(List.of(player)) == 96, "Requested96 was normalized by adapter");
        PortablePerPlayerChunksHooks.observeOptions(player, new Info(1));
        check(distance(type, map, player) == 1, "Positive player choice was raised to an adapter floor");
        PortablePerPlayerChunksHooks.observeOptions(player, new Info(0));
        check(distance(type, map, player) == 64, "Zero capture retained an old reduction or fabricated fallback");
        PortablePerPlayerChunksHooks.observeOptions(player, new Info(12));
        check(distance(type, map, player) == 12, "Valid player choice was ignored");
        PortablePerPlayerChunksHooks.observeOptions(player, new Object());
        check(distance(type, map, player) == 64, "Missing option getter retained an old reduction");
        check(PortablePerPlayerChunksHooks.radiusFor(new Object(), 64, new Object()) == 64, "Unknown objects fabricated a radius");
        check(PortablePerPlayerChunksHooks.radiusFor(map, 0, player) == 0 && PortablePerPlayerChunksHooks.radiusFor(map, -7, player) == -7,
            "Native nonpositive field operand was replaced");
        type.getMethod("setBiasedDistance", int.class).invoke(map, 63);
        PortablePerPlayerChunksHooks.observeOptions(player, new Info(Integer.MAX_VALUE));
        check(distance(type, map, player) == 64, "Native bias overflow fabricated a smaller radius");
        check(type.getField("viewDistance").getInt(map) == 64, "Observation rewrote native server field");
        type.getMethod("handChunk", Player.class, Scratch.class, Chunk.class).invoke(map, player, new Scratch(), new Chunk(64, 0));
        type.getMethod("tick").invoke(map);
        check(player.received.contains(key(64, 0)), "Modded native edge64 was filtered by an adapter32 range");
        type.getMethod("setViewDistance", int.class).invoke(map, Integer.MAX_VALUE);
        check(PortablePerPlayerChunksHooks.shouldSend(map, new Player(map), new Chunk(64, 0)),
            "Native positive radius overflow created an adapter delivery cutoff");

        // A complete ordinary native radius32 square exceeds the finite hold.
        // Overflow must execute native delivery immediately, never discard it.
        type.getMethod("setViewDistance", int.class).invoke(map, 32);
        Player stress = new Player(map); Set<Long> expected = new HashSet<>();
        for (int x = -32; x <= 32; x++) for (int z = -32; z <= 32; z++) {
            expected.add(key(x, z));
            type.getMethod("handChunk", Player.class, Scratch.class, Chunk.class).invoke(map, stress, new Scratch(), new Chunk(x, z));
        }
        int limit = constant("HOLD_LIMIT"), rate = constant("CHUNKS_PER_TICK");
        check(expected.size() == 4225 && limit < expected.size(), "Stress no longer exercises overflow");
        check(held() == limit, "Finite hold was enlarged or did not fill");
        check(stress.received.size() == expected.size() - limit, "Queue-full path discarded native overflow chunks");
        int ticks = 0, previous = stress.received.size();
        while (held() > 0 && ticks <= expected.size()) {
            type.getMethod("tick").invoke(map); ticks++;
            int sent = stress.received.size() - previous;
            check(sent > 0 && sent <= rate, "Pacing rate changed, stalled or exceeded its native send budget");
            previous = stress.received.size();
            check(held() <= limit, "Finite queue bound escaped while pumping");
        }
        check(stress.received.equals(expected), "Full native view contains missing or duplicate range chunks");
        check(ticks == (limit + rate - 1) / rate, "Held work was not drained at the existing budget");
        check(type.getField("viewDistance").getInt(map) == 32, "Pacing mutated native range");
        System.out.println("NATIVE_DISTANCE_AND_PACING_PASSED mode=" + args[0] + " checks=" + checks + " nativeView=32 offered=4225 hold=" + limit + " immediate=" + (4225 - limit) + " ticks=" + ticks + " delivered=" + stress.received.size());
    }
    static int distance(Class<?> type, Object map, Player player) throws Exception { return (Integer) type.getMethod("distanceFor", Player.class).invoke(map, player); }
    static int constant(String name) throws Exception { Field field = PortablePerPlayerChunksHooks.class.getDeclaredField(name); field.setAccessible(true); return field.getInt(null); }
    static int held() throws Exception {
        Field field = PortablePerPlayerChunksHooks.class.getDeclaredField("HELD"); field.setAccessible(true);
        int count = 0; for (Object pending : ((Map<?, ?>) field.get(null)).values()) { Field queue = pending.getClass().getDeclaredField("queue"); queue.setAccessible(true); count += ((Collection<?>) queue.get(pending)).size(); } return count;
    }
    public static long key(int x, int z) { return ((long) z << 32) | (x & 0xffffffffL); }
    static void configure() {
        property("perPlayerChunksEnabled", "true"); property("chunkPacingEnabled", "true");
        property("chunkMapClasses", TARGET); property("serverPlayerClasses", Player.class.getName()); property("trackedEntityClasses", "none");
        property("chunkViewDistanceFields", "viewDistance"); property("movePlayerMethods", "distanceFor");
        property("movePlayerDescriptors", "(LChunkPacingDistanceProbe$Player;)I");
        property("chunkSetViewDistanceMethods", "setViewDistance,setBiasedDistance"); property("chunkSetViewDistanceDescriptors", "(I)V");
        property("playerLoadedChunkMethods", "handChunk"); property("levelChunkClasses", Chunk.class.getName());
        property("chunkMapTickMethods", "tick"); property("chunkMapTickDescriptors", "()V");
        property("playerUuidMethods", "getUUID"); property("chunkGetPosMethods", "getPos"); property("chunkPositionMethods", "chunkPosition");
        property("chunkPosXFields", "x"); property("chunkPosZFields", "z"); property("clientViewDistanceMethods", "viewDistance");
        property("serverLevelMethods", "serverLevel"); property("getChunkSourceMethods", "getChunkSource"); property("chunkMapFields", "map"); property("playerLatencyFields", "latency");
    }
    public static final class Loader extends ClassLoader { public Class<?> define(byte[] bytes) { return defineClass(TARGET, bytes, 0, bytes.length); } }
    public static class NativeMap {
        public int viewDistance;
        public int distanceFor(Player player) { return viewDistance; }
        public int unrelatedDistance(Player player) { return viewDistance; }
        public void setViewDistance(int value) { viewDistance = value; }
        public void setBiasedDistance(int value) { viewDistance = value + 1; }
        public void handChunk(Player player, Scratch scratch, Chunk chunk) {
            if (!player.received.add(((long) chunk.position.z << 32) | (chunk.position.x & 0xffffffffL))) throw new AssertionError("Duplicate native chunk delivery");
        }
        public void tick() { }
    }
    public static final class Player {
        public final Set<Long> received = new HashSet<>(); public final UUID uuid = UUID.randomUUID(); public final Level level; public int latency;
        public Player(Object map) { level = new Level(map); }
        public UUID getUUID() { return uuid; } public Pos chunkPosition() { return new Pos(0, 0); } public Level serverLevel() { return level; }
    }
    public static final class Level { public final Source source; public Level(Object map) { source = new Source(map); } public Source getChunkSource() { return source; } }
    public static final class Source { public Object map; public Source(Object map) { this.map = map; } }
    public static final class Info { final int value; public Info(int value) { this.value = value; } public int viewDistance() { return value; } }
    public static final class Pos { public int x, z; public Pos(int x, int z) { this.x = x; this.z = z; } }
    public static final class Chunk { public final Pos position; public Chunk(int x, int z) { position = new Pos(x, z); } public Pos getPos() { return position; } }
    public static final class Scratch { public Scratch() { } }
}
