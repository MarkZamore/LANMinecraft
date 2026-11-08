import java.io.File;
import java.lang.reflect.InvocationTargetException;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.charset.Charset;
import java.util.ArrayList;
import org.lwjgl.openal.AL;
import org.lwjgl.openal.AL10;

/** Bounded audio initialization; never launches Minecraft's main method or opens a world. */
public final class NativeAudioProbe {
    public static void main(String[] args) throws Exception {
        String mode = System.getProperty("lanminecraft.probe.mode", "direct");
        System.out.println("file.encoding=" + System.getProperty("file.encoding"));
        System.out.println("Charset.defaultCharset=" + Charset.defaultCharset().name());
        if (!"UTF-8".equals(Charset.defaultCharset().name())) throw new AssertionError("UTF-8 was lost");
        ClassLoader loader;
        URLClassLoader isolated = null;
        if (mode.equals("knot")) {
            Class<?> env = Class.forName("net.fabricmc.api.EnvType");
            Object client = env.getField("CLIENT").get(null);
            Class<?> knotType = Class.forName("net.fabricmc.loader.impl.launch.knot.Knot");
            Object knot = knotType.getConstructor(env).newInstance(client);
            loader = (ClassLoader) knotType.getMethod("init", String[].class).invoke(knot, (Object) args);
            Path probe = Paths.get(System.getProperty("lanminecraft.probe.classes"));
            knotType.getMethod("addToClassPath", Path.class, String[].class).invoke(knot, probe, new String[0]);
            System.out.println("Knot.init=PASS");
        } else if (mode.equals("isolated")) {
            ArrayList<URL> urls = new ArrayList<URL>();
            for (String path : System.getProperty("java.class.path").split(File.pathSeparator))
                urls.add(new File(path).toURI().toURL());
            isolated = new URLClassLoader(urls.toArray(new URL[0]), ClassLoader.getSystemClassLoader().getParent());
            loader = isolated;
        } else loader = ClassLoader.getSystemClassLoader();
        try {
            Class<?> playback = Class.forName("NativeAudioProbe$Playback", true, loader);
            playback.getMethod("run").invoke(null);
            System.out.println("probe.mode=" + mode + "; PASS");
        } catch (InvocationTargetException failure) {
            failure.getCause().printStackTrace();
            throw failure;
        } finally { if (isolated != null) isolated.close(); }
    }

    public static final class Playback {
        public static void run() throws Exception {
            System.out.println("AL.classLoader=" + AL.class.getClassLoader().getClass().getName());
            try {
                AL.create();
                System.out.println("AL.create=PASS");
                int buffer = AL10.alGenBuffers(), source = AL10.alGenSources();
                try {
                    ByteBuffer silence = ByteBuffer.allocateDirect(44100 * 2).order(ByteOrder.nativeOrder());
                    AL10.alBufferData(buffer, AL10.AL_FORMAT_MONO16, silence, 44100);
                    AL10.alSourcei(source, AL10.AL_BUFFER, buffer);
                    AL10.alSourcef(source, AL10.AL_GAIN, 0);
                    AL10.alSourcePlay(source);
                    Thread.sleep(150);
                    int state = AL10.alGetSourcei(source, AL10.AL_SOURCE_STATE);
                    float offset = AL10.alGetSourcef(source, 0x1024);
                    int error = AL10.alGetError();
                    if (state != AL10.AL_PLAYING || offset <= 0 || error != AL10.AL_NO_ERROR)
                        throw new AssertionError("PCM did not advance: " + state + ", " + offset + ", " + error);
                    System.out.println("PCM.advance=PASS; seconds=" + offset);
                } finally { AL10.alDeleteSources(source); AL10.alDeleteBuffers(buffer); }
            } finally { AL.destroy(); }
        }
    }
}
