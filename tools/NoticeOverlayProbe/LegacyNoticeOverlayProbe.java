import org.lwjgl.opengl.*;
import org.lwjgl.BufferUtils;
import com.sun.jna.Native;
import com.sun.jna.Pointer;
import com.sun.jna.win32.StdCallLibrary;
import java.awt.image.BufferedImage;
import java.io.*;
import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import javax.imageio.ImageIO;

public class LegacyNoticeOverlayProbe {
    interface User32 extends StdCallLibrary { Pointer GetForegroundWindow(); boolean SetForegroundWindow(Pointer window); }
    static final User32 USER32 = Native.load("user32", User32.class);
    static int W=800, H=600;
    static int texture, fbo, vao, array, unpack;
    static final Path directory=Paths.get(System.getProperty("lanminecraft.notice.directory"));
    static String pointer(Pointer value) { return String.valueOf(Pointer.nativeValue(value)); }
    static boolean multiViewport() { return GLContext.getCapabilities().OpenGL41 || GLContext.getCapabilities().GL_ARB_viewport_array; }
    static Map<String,String> state() {
        Map<String,String> state=new LinkedHashMap<String,String>();
        int[] scalars={GL20.GL_CURRENT_PROGRAM,GL30.GL_VERTEX_ARRAY_BINDING,GL15.GL_ARRAY_BUFFER_BINDING,
            GL15.GL_ELEMENT_ARRAY_BUFFER_BINDING,GL30.GL_DRAW_FRAMEBUFFER_BINDING,GL30.GL_READ_FRAMEBUFFER_BINDING,
            GL13.GL_ACTIVE_TEXTURE,GL11.GL_TEXTURE_BINDING_2D,GL21.GL_PIXEL_UNPACK_BUFFER_BINDING,
            GL11.GL_UNPACK_ALIGNMENT,GL11.GL_UNPACK_ROW_LENGTH,GL11.GL_UNPACK_SKIP_ROWS,GL11.GL_UNPACK_SKIP_PIXELS,
            GL14.GL_BLEND_SRC_RGB,GL14.GL_BLEND_DST_RGB,GL14.GL_BLEND_SRC_ALPHA,GL14.GL_BLEND_DST_ALPHA,
            GL20.GL_BLEND_EQUATION_RGB,GL20.GL_BLEND_EQUATION_ALPHA,GL11.GL_DRAW_BUFFER,GL11.GL_READ_BUFFER};
        for(int scalar:scalars) state.put(Integer.toString(scalar),Integer.toString(GL11.glGetInteger(scalar)));
        int[] caps={GL11.GL_BLEND,GL11.GL_CULL_FACE,GL11.GL_DEPTH_TEST,GL11.GL_SCISSOR_TEST,GL11.GL_STENCIL_TEST,GL30.GL_RASTERIZER_DISCARD,GL30.GL_FRAMEBUFFER_SRGB};
        for(int cap:caps) state.put("enabled"+cap,Boolean.toString(GL11.glIsEnabled(cap)));
        IntBuffer ints=BufferUtils.createIntBuffer(16);
        GL11.glGetInteger(GL11.GL_VIEWPORT,ints); state.put("viewport",Arrays.toString(new int[]{ints.get(0),ints.get(1),ints.get(2),ints.get(3)}));
        ints.clear(); GL11.glGetInteger(GL11.GL_SCISSOR_BOX,ints); state.put("scissor",Arrays.toString(new int[]{ints.get(0),ints.get(1),ints.get(2),ints.get(3)}));
        ByteBuffer bools=BufferUtils.createByteBuffer(16);
        GL11.glGetBoolean(GL11.GL_COLOR_WRITEMASK,bools); state.put("colorMask",Arrays.toString(new int[]{bools.get(0),bools.get(1),bools.get(2),bools.get(3)}));
        state.put("depthMask",Boolean.toString(GL11.glGetBoolean(GL11.GL_DEPTH_WRITEMASK)));
        GL13.glActiveTexture(GL13.GL_TEXTURE0); state.put("texture0",Integer.toString(GL11.glGetInteger(GL11.GL_TEXTURE_BINDING_2D)));
        GL13.glActiveTexture(Integer.parseInt(state.get(Integer.toString(GL13.GL_ACTIVE_TEXTURE))));
        if(multiViewport()) {
            FloatBuffer floats=BufferUtils.createFloatBuffer(16);
            for(int index=0;index<2;index++) {
                floats.clear();GL41.glGetFloat(GL11.GL_VIEWPORT,index,floats);
                state.put("indexedViewport"+index,Arrays.toString(new float[]{floats.get(0),floats.get(1),floats.get(2),floats.get(3)}));
                ints.clear();GL30.glGetInteger(GL11.GL_SCISSOR_BOX,index,ints);
                state.put("indexedScissor"+index,Arrays.toString(new int[]{ints.get(0),ints.get(1),ints.get(2),ints.get(3)}));
                state.put("indexedScissorEnabled"+index,Boolean.toString(GL30.glIsEnabledi(GL11.GL_SCISSOR_TEST,index)));
            }
        }
        return state;
    }
    static void require(boolean test,String message) { if(!test) throw new IllegalStateException(message); }
    static void clearDefault() {
        GL30.glBindFramebuffer(GL30.GL_FRAMEBUFFER,0); GL11.glDrawBuffer(GL11.GL_BACK);
        GL11.glDisable(GL11.GL_SCISSOR_TEST); GL11.glDisable(GL30.GL_RASTERIZER_DISCARD); GL11.glColorMask(true,true,true,true);
        GL11.glClearColor(0.04f,0.08f,0.16f,1f); GL11.glClear(GL11.GL_COLOR_BUFFER_BIT);
    }
    static void awkwardState() {
        GL30.glBindFramebuffer(GL30.GL_FRAMEBUFFER,fbo);
        GL11.glDrawBuffer(GL30.GL_COLOR_ATTACHMENT0);GL11.glReadBuffer(GL30.GL_COLOR_ATTACHMENT0);
        GL30.glBindVertexArray(vao); GL15.glBindBuffer(GL15.GL_ARRAY_BUFFER,array);
        GL15.glBindBuffer(GL21.GL_PIXEL_UNPACK_BUFFER,unpack);
        GL13.glActiveTexture(GL13.GL_TEXTURE0+7); GL11.glBindTexture(GL11.GL_TEXTURE_2D,texture);
        GL11.glViewport(3,5,700,500); GL11.glScissor(11,17,101,91); GL11.glEnable(GL11.GL_SCISSOR_TEST);
        if(multiViewport()) {
            GL41.glViewportIndexedf(0,3.25f,5.5f,700.75f,500.5f);
            GL41.glViewportIndexedf(1,19.25f,23.5f,129.75f,87.5f);
            GL41.glScissorIndexed(1,31,37,141,97);
            GL30.glEnablei(GL11.GL_SCISSOR_TEST,1);
        }
        GL11.glEnable(GL11.GL_DEPTH_TEST);GL11.glEnable(GL11.GL_CULL_FACE);GL11.glEnable(GL11.GL_STENCIL_TEST);
        GL11.glEnable(GL30.GL_RASTERIZER_DISCARD); GL11.glEnable(GL30.GL_FRAMEBUFFER_SRGB);
        GL11.glEnable(GL11.GL_BLEND); GL14.glBlendFuncSeparate(GL11.GL_ONE,GL11.GL_ZERO,GL11.GL_DST_ALPHA,GL11.GL_ONE_MINUS_DST_ALPHA);
        GL20.glBlendEquationSeparate(GL14.GL_FUNC_REVERSE_SUBTRACT,GL14.GL_FUNC_SUBTRACT);
        GL11.glDepthMask(false); GL11.glColorMask(true,false,true,false);
        GL11.glPixelStorei(GL11.GL_UNPACK_ALIGNMENT,8);GL11.glPixelStorei(GL11.GL_UNPACK_ROW_LENGTH,17);
        GL11.glPixelStorei(GL11.GL_UNPACK_SKIP_ROWS,2);GL11.glPixelStorei(GL11.GL_UNPACK_SKIP_PIXELS,3);
    }
    static int capture(Path output) throws Exception {
        GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER,0); GL11.glReadBuffer(GL11.GL_FRONT);
        GL11.glPixelStorei(GL11.GL_PACK_ALIGNMENT,1);
        ByteBuffer pixels=BufferUtils.createByteBuffer(W*H*4);
        GL11.glReadPixels(0,0,W,H,GL11.GL_RGBA,GL11.GL_UNSIGNED_BYTE,pixels);
        BufferedImage image=new BufferedImage(W,H,BufferedImage.TYPE_INT_ARGB);
        int changes=0;
        int background=(pixels.get(0)&255)<<16|(pixels.get(1)&255)<<8|(pixels.get(2)&255);
        for(int y=0;y<H;y++)for(int x=0;x<W;x++) {
            int index=(y*W+x)*4; int r=pixels.get(index)&255,g=pixels.get(index+1)&255,b=pixels.get(index+2)&255;
            image.setRGB(x,H-y-1,0xff000000|(r<<16)|(g<<8)|b);
            if((((r<<16)|(g<<8)|b)^background)!=0) changes++;
        }
        ImageIO.write(image,"png",output.toFile()); return changes;
    }
    public static void main(String[] args) throws Exception {
        Files.createDirectories(directory);
        long start=System.currentTimeMillis();
        String text="count=1\n0.title=Друг MarkZamore\n0.body=Зашёл в сборку. Проверка полноэкранного вывода.\n0.createdUnixMs="+start+"\n";
        Files.write(directory.resolve("notices.properties"),text.getBytes(StandardCharsets.UTF_8));
        Pointer previousForeground=USER32.GetForegroundWindow();
        boolean fullscreen=Arrays.asList(args).contains("fullscreen");
        if(fullscreen) {Display.setDisplayMode(Display.getDesktopDisplayMode());W=Display.getDisplayMode().getWidth();H=Display.getDisplayMode().getHeight();Display.setFullscreen(true);}
        else {Display.setDisplayMode(new DisplayMode(W,H));Display.setLocation(-32000,-32000);}
        Display.setTitle("LANMinecraft legacy notification rendering test");
        Display.create(new PixelFormat(),new ContextAttribs(2,1));
        try {
            Display.setVSyncEnabled(false);
            require(!fullscreen||Display.isFullscreen(),"Legacy fullscreen flag missing");
            Thread.sleep(100);
            String foreground=pointer(USER32.GetForegroundWindow());
            System.out.println("fullscreen="+fullscreen+" monitor="+Display.isFullscreen()+" foregroundBefore="+pointer(previousForeground)+" foregroundAfterInit="+foreground);
            System.out.println("GL_VERSION="+GL11.glGetString(GL11.GL_VERSION));
            texture=GL11.glGenTextures(); GL11.glBindTexture(GL11.GL_TEXTURE_2D,texture);
            GL11.glTexImage2D(GL11.GL_TEXTURE_2D,0,GL11.GL_RGBA8,32,32,0,GL11.GL_RGBA,GL11.GL_UNSIGNED_BYTE,(ByteBuffer)null);
            fbo=GL30.glGenFramebuffers(); GL30.glBindFramebuffer(GL30.GL_FRAMEBUFFER,fbo);
            GL30.glFramebufferTexture2D(GL30.GL_FRAMEBUFFER,GL30.GL_COLOR_ATTACHMENT0,GL11.GL_TEXTURE_2D,texture,0);
            require(GL30.glCheckFramebufferStatus(GL30.GL_FRAMEBUFFER)==GL30.GL_FRAMEBUFFER_COMPLETE,"Test FBO incomplete");
            vao=GL30.glGenVertexArrays(); array=GL15.glGenBuffers();
            unpack=GL15.glGenBuffers();GL15.glBindBuffer(GL21.GL_PIXEL_UNPACK_BUFFER,unpack);GL15.glBufferData(GL21.GL_PIXEL_UNPACK_BUFFER,65536,GL15.GL_STATIC_DRAW);
            int changed=0;
            for(int frame=0;frame<80;frame++) {
                clearDefault();awkwardState();Map<String,String> before=state();
                Display.swapBuffers();
                Map<String,String> after=state();require(before.equals(after),"GL state differs at frame"+frame+" before="+before+" after="+after);
                int error=GL11.glGetError();require(error==0,"GL error "+error+" at frame "+frame);
                if(frame==60) changed=capture(directory.resolve("rendered.png"));
                Thread.sleep(25);
            }
            require(foreground.equals(pointer(USER32.GetForegroundWindow())),"Foreground HWND changed");
            Properties heartbeat=new Properties();
            try(Reader input=Files.newBufferedReader(directory.resolve("heartbeat.properties"),StandardCharsets.UTF_8)) {heartbeat.load(input);}
            require(System.currentTimeMillis()-Long.parseLong(heartbeat.getProperty("timestampUnixMs"))<1500,"No recent successful frame heartbeat");
            require(Integer.parseInt(heartbeat.getProperty("renderedCount"))==1,"Heartbeat did not acknowledge the rendered notice");
            require(changed>3000 && changed<W*H/2,"Overlay missing from captured frame: changed pixels="+changed);
            System.out.println("PASS pixels="+changed+" foreground="+foreground+" stateRestored=true");
        } finally { Display.destroy();USER32.SetForegroundWindow(previousForeground); }
    }
}


