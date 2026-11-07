import java.io.File;
import java.net.URL;
import java.net.URLClassLoader;
public final class InitialWindowProbe {
    public static void main(String[] args) throws Exception {
        URLClassLoader loader=new URLClassLoader(new URL[]{new File(args[0]).toURI().toURL()},null);
        if(args[1].equals("legacy")) {
            Class<?> display=Class.forName("org.lwjgl.opengl.WindowsDisplay",true,loader);
            Object window=display.getConstructor().newInstance();
            display.getMethod("createWindow").invoke(window);
            require(display.getField("shown").getBoolean(null),"Legacy window did not show");
            require(System.getProperty("lanminecraft.window.title").equals(display.getField("title").get(null)),"Legacy title was reset");
            display.getMethod("setIcon",java.nio.ByteBuffer[].class).invoke(window,(Object)new java.nio.ByteBuffer[0]);
        } else {
            Class<?> glfw=Class.forName("org.lwjgl.glfw.GLFW",true,loader);
            java.lang.reflect.Method create=glfw.getMethod("nglfwCreateWindow",int.class,int.class,long.class,long.class,long.class);
            glfw.getMethod("glfwDefaultWindowHints").invoke(null);
            long first=((Long)create.invoke(null,800,600,3L,0L,0L)).longValue();
            require(first==7,"Window handle changed");
            require(glfw.getField("visible").getBoolean(null),"Visible window stayed hidden");
            require(glfw.getField("shows").getInt(null)==1,"Window shown more than once");
            if(args[1].equals("disabled")) {
                require(!glfw.getField("icon").getBoolean(null),"Disabled window was changed");
                require("Minecraft 26.3".equals(glfw.getField("title").get(null)),"Disabled title was changed");
            } else {
                glfw.getMethod("nglfwSetWindowTitle",long.class,long.class).invoke(null,7L,3L);
                require(System.getProperty("lanminecraft.window.title").equals(glfw.getField("title").get(null)),"A later title update won");
                if(!args[1].equals("icon-failure")) {
                    Class<?> buffer=Class.forName("org.lwjgl.glfw.GLFWImage$Buffer",true,loader);
                    glfw.getMethod("glfwSetWindowIcon",long.class,buffer).invoke(null,7L,null);
                    require(glfw.getField("icon").getBoolean(null),"A later loader reset removed the icon");
                }
                require(glfw.getField("hintVisible").getBoolean(null),"Visibility hint was not restored");
                glfw.getMethod("glfwWindowHint",int.class,int.class).invoke(null,0x20004,0);
                create.invoke(null,800,600,3L,0L,0L);
                require(!glfw.getField("visible").getBoolean(null),"Intentionally hidden window was shown");
                require(glfw.getField("shows").getInt(null)==1,"Hidden window got a taskbar entry");
                glfw.getField("fail").setBoolean(null,true);
                require(((Long)create.invoke(null,800,600,3L,0L,0L)).longValue()==0,"Failed creation changed its result");
                require(!glfw.getField("hintVisible").getBoolean(null),"Failure changed the visibility hint");
                require(glfw.getField("shows").getInt(null)==1,"Failed creation tried to show a window");
                glfw.getField("fail").setBoolean(null,false);
                glfw.getMethod("glfwDefaultWindowHints").invoke(null);
                glfw.getMethod("glfwWindowHint",int.class,int.class).invoke(null,0x20001,0);
                create.invoke(null,800,600,3L,0L,0L);
                require(!glfw.getField("focused").getBoolean(null),"An unfocused creation stole focus");
                require(glfw.getField("focusOnShow").getBoolean(null),"Future ShowWindow focus policy was changed");
            }
        }
        loader.close();
        System.out.println("WINDOW_INITIAL_PRESENTATION_PASS");
    }
    private static void require(boolean condition,String message) { if(!condition) throw new AssertionError(message); }
}
