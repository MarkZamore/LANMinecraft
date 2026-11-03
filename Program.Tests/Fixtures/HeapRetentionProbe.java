import java.lang.management.ManagementFactory;
import java.lang.management.MemoryMXBean;
import java.lang.management.MemoryUsage;
import javax.management.MBeanServer;
import javax.management.ObjectName;

// Small synthetic heap only: no game, world, personal settings, or network.
public final class HeapRetentionProbe {
    private static volatile byte[][] live;
    private static final MemoryMXBean MEMORY = ManagementFactory.getMemoryMXBean();
    private static final MBeanServer SERVER = ManagementFactory.getPlatformMBeanServer();
    private static ObjectName diagnostic;
    private static boolean concurrent;

    private static void collect() throws Exception {
        collect(concurrent ? "gcRun" : "gcClassHistogram");
    }

    private static void collect(String command) throws Exception {
        // Histogram requests a genuine Full GC, including on Java 8 where
        // GC.run respects DisableExplicitGC. Discard the returned histogram;
        // this fixture neither changes production flags nor writes heap dumps.
        SERVER.invoke(diagnostic, command, new Object[] { new String[0] },
                      new String[] { "[Ljava.lang.String;" });
    }

    private static void sample(String phase) {
        MemoryUsage heap = MEMORY.getHeapMemoryUsage();
        long collections = 0;
        for (java.lang.management.GarbageCollectorMXBean gc : ManagementFactory.getGarbageCollectorMXBeans())
            collections += Math.max(0, gc.getCollectionCount());
        System.out.println("HEAP=" + phase + ":" + heap.getCommitted() + ":" + heap.getUsed() + ":" + collections);
    }

    private static void allocate(int chunks) {
        live = new byte[chunks][];
        for (int n = 0; n < chunks; n++) {
            // Humongous for both launcher region sizes (8/32 MiB): the
            // concurrent path can reclaim these without a mixed collection.
            live[n] = new byte[20 * 1024 * 1024];
            for (int offset = 0; offset < live[n].length; offset += 4096) live[n][offset] = (byte) n;
        }
    }

    public static void main(String[] args) throws Exception {
        diagnostic = new ObjectName("com.sun.management:type=DiagnosticCommand");
        concurrent = args[0].equals("concurrent");
        System.out.println("JAVA=" + System.getProperty("java.version"));
        System.out.println("FLAGS=" + ManagementFactory.getRuntimeMXBean().getInputArguments());
        collect();
        sample("start");
        for (int cycle = 0; cycle < 2; cycle++) {
            allocate(4 + cycle * 2);
            sample("allocated" + cycle);
            collect();
            sample("live" + cycle);
            live = null;
            for (int gc = 0; gc < 3; gc++) {
                collect();
                sample("freed" + cycle + "_" + gc);
            }
        }
        // Java 8 does not shrink at concurrent Remark even in the default
        // control. Finish with Full GC to exercise its actual shrink site too.
        collect("gcClassHistogram");
        sample("finalFull");
    }
}
