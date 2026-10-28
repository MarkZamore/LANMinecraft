import java.io.InputStream;
import java.util.*;
import java.util.concurrent.Executor;
import minecraft.portable.identity.PortableLanAutoPublishTransformer;

/** Executes the real common Forge hook after a vanilla or author-cancelled init. */
public final class ForgeLanReturnProbe {
    private static final String TARGET = "ForgeLanReturnProbe$ShareScreen";
    private static void check(boolean value, String reason) { if (!value) throw new AssertionError(reason); }
    public static void main(String[] args) throws Exception {
        Map<String,String> names = Map.ofEntries(
            Map.entry("lanPublishEnabled", "true"), Map.entry("lanShareScreenClasses", TARGET),
            Map.entry("lanShareInitMethods", "init"), Map.entry("minecraftClasses", Minecraft.class.getName()),
            Map.entry("screenClasses", BaseScreen.class.getName()), Map.entry("gameTypeClasses", GameType.class.getName()),
            Map.entry("httpUtilClasses", HttpUtil.class.getName()), Map.entry("publishCommandClasses", PublishCommand.class.getName()),
            Map.entry("componentClasses", Component.class.getName()), Map.entry("minecraftGetInstanceMethods", "getInstance"),
            Map.entry("getSingleplayerServerMethods", "getSingleplayerServer"), Map.entry("isPublishedMethods", "isPublished"),
            Map.entry("getDefaultGameTypeMethods", "getDefaultGameType"), Map.entry("getWorldDataMethods", "getWorldData"),
            Map.entry("isAllowCommandsMethods", "getAllowCommands"), Map.entry("getAvailablePortMethods", "getAvailablePort"),
            Map.entry("publishServerMethods", "publishServer"), Map.entry("setScreenMethods", "setScreen"),
            Map.entry("publishSuccessMethods", "getSuccessMessage"), Map.entry("minecraftGuiFields", "gui"),
            Map.entry("guiChatMethods", "getChat"), Map.entry("chatAddMessageMethods", "addMessage"),
            Map.entry("updateTitleMethods", "updateTitle"));
        names.forEach((name,value) -> System.setProperty("minecraft.portable.identity."+name,value));
        byte[] original;
        try(InputStream in=ForgeLanReturnProbe.class.getResourceAsStream("/"+TARGET+".class")){original=in.readAllBytes();}
        byte[] changed=new PortableLanAutoPublishTransformer().transform(null,null,TARGET,null,null,original);
        Class<?> patched=new ClassLoader(ForgeLanReturnProbe.class.getClassLoader()) {
            Class<?> define(){return defineClass(TARGET,changed,0,changed.length);}
        }.define();
        for(boolean redirect:new boolean[]{false,true}) {
            Minecraft.INSTANCE=new Minecraft();
            Minecraft client=Minecraft.INSTANCE;
            BaseScreen screen=(BaseScreen)patched.getConstructor().newInstance();
            screen.redirect=redirect; client.current=screen;
            screen.init();
            check(screen.initializations==1,"Native init did not run");
            check(client.authorForms==(redirect?1:0),"Author redirect branch did not initialize its form");
            check(client.server.attempts==1&&client.server.published,"Return path failed to publish");
            check(client.server.usedMode==GameType.SURVIVAL&&client.server.usedCommands,"World defaults changed");
            screen.init();
            check(client.server.attempts==1,"Resize created a second session");
            client.drain();
            check(client.current==null,"Published form was not dismissed");
            BaseScreen again=(BaseScreen)patched.getConstructor().newInstance(); again.redirect=redirect;client.current=again;
            again.init();client.drain();
            check(client.server.attempts==1&&client.current==null,"Second screen republished or stayed open");
        }
        Minecraft.INSTANCE=new Minecraft();Minecraft.INSTANCE.server.succeed=false;
        BaseScreen failed=(BaseScreen)patched.getConstructor().newInstance();failed.redirect=true;Minecraft.INSTANCE.current=failed;
        failed.init();Minecraft.INSTANCE.drain();
        check(Minecraft.INSTANCE.server.attempts==1&&Minecraft.INSTANCE.current!=null,"Failed publish hid the author's fallback form");
        System.out.println("PASS: actual Forge hook follows initialized vanilla and cancelled author-form returns; resize and repeated entry create one session; failure preserves the form. No game or Steam started.");
    }
    public static class BaseScreen {
        public int initializations;public boolean redirect,widgetsReady;
        public void init(){throw new AssertionError("Untransformed init");}
    }
    public static final class ShareScreen extends BaseScreen {
        @Override public void init(){initializations++;if(redirect){Minecraft.INSTANCE.openAuthorForm();return;}widgetsReady=true;}
    }
    public enum GameType { SURVIVAL }
    public static final class WorldData {public boolean getAllowCommands(){return true;}}
    public static final class Server {
        public int attempts;public boolean published,succeed=true,usedCommands;public GameType usedMode;
        public boolean isPublished(){return published;}
        public GameType getDefaultGameType(){return GameType.SURVIVAL;}
        public WorldData getWorldData(){return new WorldData();}
        public boolean publishServer(GameType mode,boolean commands,int port){
            check(Minecraft.INSTANCE.current.widgetsReady,"Publish preceded native/author form initialization");
            check(port==35658,"Available native port was not used");
            attempts++;usedMode=mode;usedCommands=commands;published=succeed;return succeed;
        }
    }
    public static final class Minecraft implements Executor {
        public static Minecraft INSTANCE;public final Server server=new Server();public final Gui gui=new Gui();
        public BaseScreen current;public int authorForms;public final Queue<Runnable> pending=new ArrayDeque<>();
        public static Minecraft getInstance(){return INSTANCE;}
        public Server getSingleplayerServer(){return server;}
        public void openAuthorForm(){BaseScreen author=new BaseScreen();author.widgetsReady=true;current=author;authorForms++;}
        public void setScreen(BaseScreen screen){current=screen;}
        public void updateTitle(){}
        public void execute(Runnable action){pending.add(action);}
        public void drain(){for(Runnable task;(task=pending.poll())!=null;)task.run();}
    }
    public static final class Gui {public Chat getChat(){return new Chat();}}
    public static final class Chat {public void addMessage(Component message){}}
    public static final class Component {}
    public static final class PublishCommand {public static Component getSuccessMessage(int port){return new Component();}}
    public static final class HttpUtil {public static int getAvailablePort(){return 35658;}}
}
