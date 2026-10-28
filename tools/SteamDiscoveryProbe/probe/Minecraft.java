package probe;
import java.util.concurrent.*;
public final class Minecraft implements Executor {
    private static final Minecraft INSTANCE=new Minecraft();
    private final ConcurrentLinkedQueue<Runnable> pending=new ConcurrentLinkedQueue<>();
    public static Minecraft getInstance(){return INSTANCE;}
    public void execute(Runnable task){pending.add(task);}
    public static void flush(){for(Runnable task;(task=INSTANCE.pending.poll())!=null;)task.run();}
}
