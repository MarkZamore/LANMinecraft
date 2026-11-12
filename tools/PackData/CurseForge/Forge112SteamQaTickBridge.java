package lanminecraft.qaforge;

import net.minecraftforge.fml.common.FMLCommonHandler;
import net.minecraftforge.fml.common.Mod;
import net.minecraftforge.fml.common.event.FMLInitializationEvent;
import net.minecraftforge.fml.common.eventhandler.SubscribeEvent;
import net.minecraftforge.fml.common.gameevent.TickEvent;

import java.lang.reflect.Method;

/** Test-only tick dispatch loaded by the actual Forge client class loader. */
@Mod(modid = "lanminecraft_java8_qa", name = "LANMinecraft Java 8 QA",
        version = "1", acceptedMinecraftVersions = "[1.12.2]", clientSideOnly = true)
public final class Forge112SteamQaTickBridge {
    private Method tick;

    @Mod.EventHandler
    public void initialize(FMLInitializationEvent event) throws ReflectiveOperationException {
        if (System.getProperty("lanminecraft.steamqa.run") == null
                || !Boolean.getBoolean("link.e4steam.launcherManagedLan")) return;
        Class<?> agent = Class.forName("lanminecraft.qa.Forge112SteamQaAgent", false,
                ClassLoader.getSystemClassLoader());
        tick = agent.getMethod("runClientTickTasks");
        FMLCommonHandler.instance().bus().register(this);
    }

    @SubscribeEvent
    public void onClientTick(TickEvent.ClientTickEvent event) throws ReflectiveOperationException {
        if (event.phase == TickEvent.Phase.END) tick.invoke(null);
    }
}
