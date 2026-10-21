package minecraft.portable.notice;

import java.awt.AlphaComposite;
import java.awt.Color;
import java.awt.Font;
import java.awt.FontMetrics;
import java.awt.Graphics2D;
import java.awt.RenderingHints;
import java.awt.image.BufferedImage;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.FloatBuffer;
import java.nio.IntBuffer;
import java.util.HashMap;
import java.util.Map;
import java.util.Properties;

/** Draws into the game's own default framebuffer just before its buffer swap. */
final class PortableNoticeRenderer {
    private final Class<?> api;
    private final long window;
    private final Gl gl;
    private final boolean modern;
    private long retryAfter;
    private boolean initialized;
    private boolean vaoSupported;
    private boolean gl30;
    private boolean samplerSupported;
    private int program, vao, vbo, opacityLocation;
    private final Card[] cards = new Card[3];
    private Style style;
    private final IntBuffer dimensionsX = ints(1), dimensionsY = ints(1);
    private final FloatBuffer contentScaleX = floats(new float[1]), contentScaleY = floats(new float[1]);
    private boolean contentScaleSupported = true;

    PortableNoticeRenderer(Class<?> api, long window) throws Exception {
        this.api = api;
        this.window = window;
        this.modern = window != 0;
        this.gl = new Gl(api.getClassLoader(), modern);
    }

    boolean matches(Class<?> candidate, long handle) { return api == candidate && window == handle; }
    void disable() { retryAfter = System.currentTimeMillis() + 5000; initialized = false;
        for (int index = 0; index < cards.length; index++) cards[index] = null; }

    int present(PortableNoticeHooks.Notice[] messages, long now) throws Exception {
        if (now < retryAfter) return -1;
        int active = 0;
        for (PortableNoticeHooks.Notice message : messages)
            if (now >= message.created && now - message.created < PortableNoticeHooks.LIFE_MS) active++;
        if (initialized && active == 0) return 0;
        int width, height;
        double scale = 1;
        if (modern) {
            if (((Long)gl.call(api, "glfwGetCurrentContext", new Class<?>[0])).longValue() != window) return -1;
            dimensionsX.clear(); dimensionsY.clear();
            gl.call(api, "glfwGetFramebufferSize", new Class<?>[] { long.class, IntBuffer.class, IntBuffer.class }, window, dimensionsX, dimensionsY);
            width = dimensionsX.get(0); height = dimensionsY.get(0);
            dimensionsX.clear(); dimensionsY.clear();
            gl.call(api, "glfwGetWindowSize", new Class<?>[] { long.class, IntBuffer.class, IntBuffer.class }, window, dimensionsX, dimensionsY);
            if (dimensionsX.get(0) > 0) scale = (double)width / dimensionsX.get(0);
            if (contentScaleSupported) {
                try {
                    contentScaleX.clear(); contentScaleY.clear();
                    gl.call(api, "glfwGetWindowContentScale", new Class<?>[] {long.class, FloatBuffer.class, FloatBuffer.class}, window, contentScaleX, contentScaleY);
                    scale = Math.max(scale, contentScaleX.get(0));
                } catch (NoSuchMethodException oldGlfw) { contentScaleSupported = false; }
            }
        } else {
            width = ((Integer)gl.call(api, "getWidth", new Class<?>[0])).intValue();
            height = ((Integer)gl.call(api, "getHeight", new Class<?>[0])).intValue();
            if (contentScaleSupported) {
                try { scale = ((Float)gl.call(api, "getPixelScaleFactor", new Class<?>[0])).floatValue(); }
                catch (NoSuchMethodException oldDisplay) { contentScaleSupported = false; }
            }
        }
        if (width <= 0 || height <= 0) return -1;
        if (Double.isNaN(scale) || Double.isInfinite(scale)) scale = 1;
        scale = Math.max(0.5, Math.min(4, scale));
        if (!initialized) {
            Object capabilities = gl.capabilities();
            gl30 = gl.flag(capabilities, "OpenGL30");
            vaoSupported = gl30;
            samplerSupported = gl.flag(capabilities, "OpenGL33") || gl.flag(capabilities, "GL_ARB_sampler_objects");
            style = new Style(PortableNoticeHooks.style);
        }
        State saved = new State(gl, gl30, samplerSupported, vaoSupported);
        try {
            if (!initialized) initialize();
            if (active == 0) return 0;
            gl.i("GL11", "glDisable", 2929); // depth
            gl.i("GL11", "glDisable", 2960); // stencil
            gl.i("GL11", "glDisable", 2884); // cull
            if (saved.indexedScissor) gl.ii("GL30", "glDisablei", 3089, 0);
            else gl.i("GL11", "glDisable", 3089); // scissor
            gl.i("GL11", "glDisable", 32926); // sample alpha to coverage
            gl.i("GL11", "glDisable", 32928); // sample coverage
            gl.i("GL11", "glDisable", 3058); // color logic operation
            for (int index = 0; index < saved.clipDistances; index++) gl.i("GL11", "glDisable", 12288 + index);
            if (saved.core) gl.i("GL11", "glDisable", 36433); // sample mask
            if (saved.clipControl) gl.ii("GL45", "glClipControl", 36001, 37726);
            if (gl30) {
                gl.i("GL11", "glDisable", 35977); // rasterizer discard
                gl.i("GL11", "glDisable", 36281); // framebuffer sRGB
                gl.ii("GL30", "glBindFramebuffer", 36009, 0); // draw framebuffer
            } else if (saved.extFramebuffer) {
                gl.ii("EXTFramebufferObject", "glBindFramebufferEXT", 36160, 0);
            }
            gl.i("GL11", "glDrawBuffer", 1029); // GL_BACK, after binding default framebuffer
            if (saved.indexedScissor) gl.call("GL41", "glViewportIndexedf", new Class<?>[] {int.class,float.class,float.class,float.class,float.class}, 0, 0f, 0f, (float)width, (float)height);
            else gl.call("GL11", "glViewport", new Class<?>[] { int.class, int.class, int.class, int.class }, 0, 0, width, height);
            if (gl30) gl.call("GL30", "glColorMaski", new Class<?>[] { int.class, boolean.class, boolean.class, boolean.class, boolean.class }, 0, true, true, true, true);
            else gl.call("GL11", "glColorMask", new Class<?>[] { boolean.class, boolean.class, boolean.class, boolean.class }, true, true, true, true);
            gl.call("GL11", "glDepthMask", new Class<?>[] { boolean.class }, false);
            gl.ii("GL11", "glPolygonMode", 1032, 6914); // front/back fill
            if (gl30) gl.ii("GL30", "glEnablei", 3042, 0); else gl.i("GL11", "glEnable", 3042);
            if (saved.indexedBlend) {
                gl.call(saved.blendApi, "glBlendEquationSeparatei" + saved.blendSuffix, threeInts(), 0, 32774, 32774);
                gl.call(saved.blendApi, "glBlendFuncSeparatei" + saved.blendSuffix, new Class<?>[] { int.class,int.class,int.class,int.class,int.class }, 0, 770, 771, 1, 771);
            } else {
                gl.ii("GL20", "glBlendEquationSeparate", 32774, 32774);
                gl.call("GL14", "glBlendFuncSeparate", fourInts(), 770, 771, 1, 771);
            }
            gl.i("GL13", "glActiveTexture", 33984);
            if (samplerSupported) gl.ii("GL33", "glBindSampler", 0, 0);
            gl.i("GL20", "glUseProgram", program);
            gl.ii("GL15", "glBindBuffer", 35052, 0); // pixel unpack buffer
            gl.ii("GL11", "glPixelStorei", 3317, 1);
            gl.ii("GL11", "glPixelStorei", 3314, 0);
            gl.ii("GL11", "glPixelStorei", 3315, 0);
            gl.ii("GL11", "glPixelStorei", 3316, 0);
            if (vaoSupported) gl.i("GL30", "glBindVertexArray", vao);
            gl.ii("GL15", "glBindBuffer", 34962, vaoSupported ? vbo : 0);
            if (!vaoSupported) {
                gl.i("GL13", "glClientActiveTexture", 33984);
                gl.i("GL11", "glEnableClientState", 32884);
                gl.i("GL11", "glEnableClientState", 32888);
            }

            int bottom = height - (int)Math.round(style.gap * scale);
            int slot = 0;
            for (int index = messages.length - 1; index >= 0; index--) {
                PortableNoticeHooks.Notice message = messages[index];
                long age = now - message.created;
                if (age < 0 || age >= PortableNoticeHooks.LIFE_MS) continue;
                int cardWidth = Math.max(1, Math.min((int)Math.round(style.width * scale), width - (int)Math.round(2 * style.gap * scale)));
                Card card = cards[slot];
                String key = message.key() + "\n" + cardWidth + "\n" + scale;
                if (card == null || !card.key.equals(key)) {
                    if (card != null) gl.i("GL11", "glDeleteTextures", card.texture);
                    card = rasterize(message, key, cardWidth, scale);
                    cards[slot] = card;
                }
                float opacity = (float)Math.min(1d, Math.min((double)age / style.rise,
                    (double)(PortableNoticeHooks.LIFE_MS - age) / style.fade));
                int x = Math.min((int)Math.round(style.gap * scale), Math.max(0, width - card.width));
                int y = Math.max(0, bottom - card.height);
                gl.ii("GL11", "glBindTexture", 3553, card.texture);
                gl.call("GL20", "glUniform1f", new Class<?>[] { int.class, float.class }, opacityLocation, opacity);
                draw(x, y, card.width, card.height, width, height);
                bottom = y - (int)Math.round(style.gap * scale);
                slot++;
            }
            return slot;
        } finally {
            saved.restore();
        }
    }

    private void initialize() throws Exception {
        boolean core = gl30 && gl.flag(gl.capabilities(), "OpenGL32");
        String vertex = core
            ? "#version 150\nin vec2 position;in vec2 uv;out vec2 texcoord;void main(){gl_Position=vec4(position,0,1);texcoord=uv;}"
            : "#version 120\nvarying vec2 texcoord;void main(){gl_Position=gl_Vertex;texcoord=gl_MultiTexCoord0.xy;}";
        String fragment = core
            ? "#version 150\nuniform sampler2D image;uniform float opacity;in vec2 texcoord;out vec4 color;void main(){color=texture(image,texcoord)*vec4(1,1,1,opacity);}"
            : "#version 120\nuniform sampler2D image;uniform float opacity;varying vec2 texcoord;void main(){gl_FragColor=texture2D(image,texcoord)*vec4(1,1,1,opacity);}";
        // A GL3.0 compatibility context has VAOs but uses GLSL120's conventional
        // client attributes. Use the same GL2 path there, with client state saved.
        if (!core) vaoSupported = false;
        int vs = shader(35633, vertex), fs = shader(35632, fragment);
        program = gl.integer("GL20", "glCreateProgram");
        try {
            gl.ii("GL20", "glAttachShader", program, vs); gl.ii("GL20", "glAttachShader", program, fs);
            if (core) {
                gl.call("GL20", "glBindAttribLocation", new Class<?>[] { int.class, int.class, CharSequence.class }, program, 0, "position");
                gl.call("GL20", "glBindAttribLocation", new Class<?>[] { int.class, int.class, CharSequence.class }, program, 1, "uv");
            }
            gl.i("GL20", "glLinkProgram", program);
            if (((Integer)gl.call("GL20", "glGetProgrami", twoInts(), program, 35714)).intValue() == 0)
                throw new IllegalStateException("Notification shader link failed: " + gl.call("GL20", "glGetProgramInfoLog", twoInts(), program, 8192));
            opacityLocation = ((Integer)gl.call("GL20", "glGetUniformLocation", new Class<?>[] { int.class, CharSequence.class }, program, "opacity")).intValue();
            gl.i("GL20", "glUseProgram", program);
            int textureLocation = ((Integer)gl.call("GL20", "glGetUniformLocation", new Class<?>[] { int.class, CharSequence.class }, program, "image")).intValue();
            gl.ii("GL20", "glUniform1i", textureLocation, 0);
            if (vaoSupported) {
                vao = gl.integer("GL30", "glGenVertexArrays");
                vbo = gl.integer("GL15", "glGenBuffers");
                gl.i("GL30", "glBindVertexArray", vao);
                gl.ii("GL15", "glBindBuffer", 34962, vbo);
                gl.i("GL20", "glEnableVertexAttribArray", 0); gl.i("GL20", "glEnableVertexAttribArray", 1);
                gl.call("GL20", "glVertexAttribPointer", new Class<?>[] { int.class, int.class, int.class, boolean.class, int.class, long.class }, 0, 2, 5126, false, 16, 0L);
                gl.call("GL20", "glVertexAttribPointer", new Class<?>[] { int.class, int.class, int.class, boolean.class, int.class, long.class }, 1, 2, 5126, false, 16, 8L);
            }
            initialized = true;
        } finally {
            gl.i("GL20", "glDeleteShader", vs); gl.i("GL20", "glDeleteShader", fs);
        }
    }

    private int shader(int kind, String source) throws Exception {
        int shader = ((Integer)gl.call("GL20", "glCreateShader", oneInt(), kind)).intValue();
        gl.call("GL20", "glShaderSource", new Class<?>[] { int.class, CharSequence.class }, shader, source);
        gl.i("GL20", "glCompileShader", shader);
        if (((Integer)gl.call("GL20", "glGetShaderi", twoInts(), shader, 35713)).intValue() == 0)
            throw new IllegalStateException("Notification shader compile failed: " + gl.call("GL20", "glGetShaderInfoLog", twoInts(), shader, 8192));
        return shader;
    }

    private Card rasterize(PortableNoticeHooks.Notice message, String key, int width, double scale) throws Exception {
        int pad = Math.max(1, (int)Math.round(style.padding * scale));
        int fontSize = Math.max(8, (int)Math.round(style.fontSize * scale));
        Font body = new Font(style.font, Font.PLAIN, fontSize);
        if ("Dialog".equals(body.getFamily()) && !"Dialog".equals(style.font)) body = new Font("Segoe UI", Font.PLAIN, fontSize);
        Font title = body.deriveFont(Font.BOLD);
        BufferedImage sizing = new BufferedImage(1, 1, BufferedImage.TYPE_INT_ARGB);
        Graphics2D measure = sizing.createGraphics();
        String titleText = fit(message.title, measure.getFontMetrics(title), width - 2 * pad);
        String bodyText = fit(message.body, measure.getFontMetrics(body), width - 2 * pad);
        int line = (int)Math.round(fontSize * 1.45);
        int height = pad * 2 + line * 2 + (int)Math.round(4 * scale);
        measure.dispose();
        BufferedImage image = new BufferedImage(width, height, BufferedImage.TYPE_INT_ARGB);
        Graphics2D painter = image.createGraphics();
        painter.setComposite(AlphaComposite.Src);
        painter.setRenderingHint(RenderingHints.KEY_ANTIALIASING, RenderingHints.VALUE_ANTIALIAS_ON);
        painter.setRenderingHint(RenderingHints.KEY_TEXT_ANTIALIASING, RenderingHints.VALUE_TEXT_ANTIALIAS_ON);
        int radius = (int)Math.round(style.radius * scale * 2);
        painter.setColor(new Color(style.background, true)); painter.fillRoundRect(0, 0, width, height, radius, radius);
        painter.setColor(new Color(style.border, true)); painter.drawRoundRect(0, 0, width - 1, height - 1, radius, radius);
        painter.setFont(title); painter.setColor(new Color(style.text, true));
        painter.drawString(titleText, pad, pad + painter.getFontMetrics().getAscent());
        painter.setFont(body); painter.setColor(new Color(style.muted, true));
        painter.drawString(bodyText, pad, pad + line + (int)Math.round(4 * scale) + painter.getFontMetrics().getAscent());
        painter.dispose();
        ByteBuffer pixels = ByteBuffer.allocateDirect(width * height * 4).order(ByteOrder.nativeOrder());
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
            int argb = image.getRGB(x, y);
            pixels.put((byte)(argb >>> 16)); pixels.put((byte)(argb >>> 8)); pixels.put((byte)argb); pixels.put((byte)(argb >>> 24));
        }
        pixels.flip();
        int texture = gl.integer("GL11", "glGenTextures");
        gl.ii("GL11", "glBindTexture", 3553, texture);
        gl.call("GL11", "glTexParameteri", threeInts(), 3553, 10241, 9729);
        gl.call("GL11", "glTexParameteri", threeInts(), 3553, 10240, 9729);
        gl.call("GL11", "glTexParameteri", threeInts(), 3553, 10242, 33071);
        gl.call("GL11", "glTexParameteri", threeInts(), 3553, 10243, 33071);
        gl.call("GL11", "glTexImage2D", new Class<?>[] { int.class, int.class, int.class, int.class, int.class, int.class, int.class, int.class, ByteBuffer.class }, 3553, 0, 6408, width, height, 0, 6408, 5121, pixels);
        return new Card(key, texture, width, height);
    }

    private void draw(int x, int y, int w, int h, int width, int height) throws Exception {
        float left = 2f * x / width - 1f, right = 2f * (x + w) / width - 1f;
        float top = 1f - 2f * y / height, bottom = 1f - 2f * (y + h) / height;
        if (vaoSupported) {
            FloatBuffer vertices = floats(new float[] { left, top, 0, 0, left, bottom, 0, 1, right, top, 1, 0, right, bottom, 1, 1 });
            gl.call("GL15", "glBufferData", new Class<?>[] { int.class, FloatBuffer.class, int.class }, 34962, vertices, 35040);
        } else {
            FloatBuffer vertices = floats(new float[] { left, top, left, bottom, right, top, right, bottom });
            FloatBuffer coordinates = floats(new float[] { 0, 0, 0, 1, 1, 0, 1, 1 });
            gl.call("GL11", "glVertexPointer", new Class<?>[] { int.class, int.class, FloatBuffer.class }, 2, 0, vertices);
            gl.call("GL11", "glTexCoordPointer", new Class<?>[] { int.class, int.class, FloatBuffer.class }, 2, 0, coordinates);
        }
        gl.call("GL11", "glDrawArrays", threeInts(), 5, 0, 4);
    }

    private static String fit(String text, FontMetrics font, int width) {
        if (font.stringWidth(text) <= width) return text;
        String dots = "\u2026";
        int length = text.length();
        while (length > 0 && font.stringWidth(text.substring(0, length) + dots) > width) length--;
        return text.substring(0, length) + dots;
    }

    private static final class Card {
        final String key; final int texture, width, height;
        Card(String key, int texture, int width, int height) { this.key = key; this.texture = texture; this.width = width; this.height = height; }
    }

    private static final class Style {
        final int width, gap, padding, radius, fontSize, background, border, text, muted, rise, fade;
        final String font;
        Style(Properties values) {
            width = bounded(values, "width", 320, 160, 640); gap = bounded(values, "gap", 24, 0, 48);
            padding = bounded(values, "padding", 16, 4, 32); radius = bounded(values, "radius", 8, 0, 32);
            fontSize = bounded(values, "fontSize", 14, 10, 28);
            font = values.getProperty("fontFamily", "Segoe UI");
            background = color(values, "bgArgb", 0xff2c2c2c); border = color(values, "borderArgb", 0x26ffffff);
            text = color(values, "textArgb", 0xffffffff); muted = color(values, "mutedArgb", 0xc5ffffff);
            rise = bounded(values, "riseMs", 200, 1, 1000); fade = bounded(values, "fadeMs", 350, 1, 1000);
        }
        private static int bounded(Properties values, String key, int fallback, int min, int max) {
            try { return Math.max(min, Math.min(max, Integer.parseInt(values.getProperty(key)))); }
            catch (Exception ignored) { return fallback; }
        }
        private static int color(Properties values, String key, int fallback) {
            try { String color = values.getProperty(key).replace("#", "").replace("0x", ""); return (int)Long.parseLong(color, 16); }
            catch (Exception ignored) { return fallback; }
        }
    }

    private static final class State {
        final Gl gl;
        final boolean framebuffer, extFramebuffer, sampler, vao, core, indexedBlend, indexedScissor, clipControl;
        final int clipOrigin, clipDepthMode, clipDistances;
        final String blendApi, blendSuffix;
        final int program, vertexArray, arrayBuffer, activeTexture, texture0, sampler0, drawFbo, defaultDrawBuffer;
        final int blendSrcRgb, blendDstRgb, blendSrcAlpha, blendDstAlpha, blendEquationRgb, blendEquationAlpha;
        final int unpackBuffer, unpackAlignment, unpackRowLength, unpackSkipRows, unpackSkipPixels;
        final int[] viewport, polygon;
        final float[] indexedViewport;
        final boolean depthMask;
        final ByteBuffer colorMask;
        final Map<Integer, Boolean> enabled = new HashMap<Integer, Boolean>();
        State(Gl gl, boolean framebuffer, boolean sampler, boolean vao) throws Exception {
            this.gl = gl; this.framebuffer = framebuffer; this.sampler = sampler; this.vao = vao;
            Object caps = gl.capabilities();
            core = gl.flag(caps, "OpenGL32");
            indexedBlend = gl.flag(caps, "OpenGL40") || gl.flag(caps, "GL_ARB_draw_buffers_blend");
            blendApi = gl.flag(caps, "OpenGL40") ? "GL40" : "ARBDrawBuffersBlend";
            blendSuffix = gl.flag(caps, "OpenGL40") ? "" : "ARB";
            indexedScissor = gl.flag(caps, "OpenGL41") || gl.flag(caps, "GL_ARB_viewport_array");
            clipControl = gl.flag(caps, "OpenGL45") || gl.flag(caps, "GL_ARB_clip_control");
            clipOrigin = clipControl ? gl.get(37724) : 0; clipDepthMode = clipControl ? gl.get(37725) : 0;
            clipDistances = Math.max(0, Math.min(8, gl.get(3378)));
            extFramebuffer = !framebuffer && gl.flag(gl.capabilities(), "GL_EXT_framebuffer_object");
            program = gl.get(35725); vertexArray = vao ? gl.get(34229) : 0; arrayBuffer = gl.get(34964);
            activeTexture = gl.get(34016);
            try {
                gl.i("GL13", "glActiveTexture", 33984);
                texture0 = gl.get(32873); sampler0 = sampler ? gl.get(35097) : 0;
            } finally { gl.i("GL13", "glActiveTexture", activeTexture); }
            drawFbo = framebuffer ? gl.get(36006) : (extFramebuffer ? gl.get(36006) : 0);
            // Draw-buffer selection belongs to the framebuffer. Capture default's
            // selection separately so restoring a custom FBO does not mutate it.
            try {
                if (framebuffer) gl.ii("GL30", "glBindFramebuffer", 36009, 0);
                else if (extFramebuffer) gl.ii("EXTFramebufferObject", "glBindFramebufferEXT", 36160, 0);
                defaultDrawBuffer = gl.get(3073);
            } finally {
                if (framebuffer) gl.ii("GL30", "glBindFramebuffer", 36009, drawFbo);
                else if (extFramebuffer) gl.ii("EXTFramebufferObject", "glBindFramebufferEXT", 36160, drawFbo);
            }
            viewport = gl.vector(2978, 4); polygon = gl.vector(2880, 2);
            indexedViewport = indexedScissor ? gl.floatVector(2978, 4) : null;
            blendSrcRgb = gl.get(32969); blendDstRgb = gl.get(32968); blendSrcAlpha = gl.get(32971); blendDstAlpha = gl.get(32970);
            blendEquationRgb = gl.get(32777); blendEquationAlpha = gl.get(34877);
            unpackBuffer = gl.get(35055); unpackAlignment = gl.get(3317); unpackRowLength = gl.get(3314);
            unpackSkipRows = gl.get(3315); unpackSkipPixels = gl.get(3316);
            depthMask = ((Boolean)gl.call("GL11", "glGetBoolean", oneInt(), 2930)).booleanValue();
            colorMask = ByteBuffer.allocateDirect(16);
            gl.call("GL11", gl.modern ? "glGetBooleanv" : "glGetBoolean", new Class<?>[] { int.class, ByteBuffer.class }, 3107, colorMask);
            int[] flags = core ? new int[] {2929, 2960, 2884, 3089, 3042, 32926, 32928, 3058, 35977, 36281, 36433}
                : framebuffer ? new int[] {2929, 2960, 2884, 3089, 3042, 32926, 32928, 3058, 35977, 36281}
                : new int[] {2929, 2960, 2884, 3089, 3042, 32926, 32928, 3058};
            for (int flag : flags) enabled.put(flag, ((Boolean)gl.call("GL11", "glIsEnabled", oneInt(), flag)).booleanValue());
            for (int index = 0; index < clipDistances; index++) enabled.put(12288 + index, ((Boolean)gl.call("GL11", "glIsEnabled", oneInt(), 12288 + index)).booleanValue());
            // Conventional arrays only exist in compatibility contexts. Save them
            // even if a GL3.0 compatibility context exposed VAOs initially.
            if (!gl.flag(gl.capabilities(), "OpenGL32")) gl.i("GL11", "glPushClientAttrib", -1);
        }
        void restore() throws Exception {
            gl.i("GL20", "glUseProgram", program);
            if (vao) gl.i("GL30", "glBindVertexArray", vertexArray);
            if (!gl.flag(gl.capabilities(), "OpenGL32")) gl.call("GL11", "glPopClientAttrib", new Class<?>[0]);
            gl.ii("GL15", "glBindBuffer", 34962, arrayBuffer);
            gl.i("GL13", "glActiveTexture", 33984);
            gl.ii("GL11", "glBindTexture", 3553, texture0);
            if (sampler) gl.ii("GL33", "glBindSampler", 0, sampler0);
            gl.i("GL13", "glActiveTexture", activeTexture);
            gl.ii("GL15", "glBindBuffer", 35052, unpackBuffer);
            gl.ii("GL11", "glPixelStorei", 3317, unpackAlignment); gl.ii("GL11", "glPixelStorei", 3314, unpackRowLength);
            gl.ii("GL11", "glPixelStorei", 3315, unpackSkipRows); gl.ii("GL11", "glPixelStorei", 3316, unpackSkipPixels);
            if (indexedScissor) gl.call("GL41", "glViewportIndexedf", new Class<?>[] {int.class,float.class,float.class,float.class,float.class}, 0, indexedViewport[0], indexedViewport[1], indexedViewport[2], indexedViewport[3]);
            else gl.call("GL11", "glViewport", fourInts(), viewport[0], viewport[1], viewport[2], viewport[3]);
            if (indexedBlend) {
                gl.call(blendApi, "glBlendFuncSeparatei" + blendSuffix, new Class<?>[] {int.class,int.class,int.class,int.class,int.class}, 0, blendSrcRgb, blendDstRgb, blendSrcAlpha, blendDstAlpha);
                gl.call(blendApi, "glBlendEquationSeparatei" + blendSuffix, threeInts(), 0, blendEquationRgb, blendEquationAlpha);
            } else {
                gl.call("GL14", "glBlendFuncSeparate", fourInts(), blendSrcRgb, blendDstRgb, blendSrcAlpha, blendDstAlpha);
                gl.ii("GL20", "glBlendEquationSeparate", blendEquationRgb, blendEquationAlpha);
            }
            gl.call("GL11", "glDepthMask", new Class<?>[] { boolean.class }, depthMask);
            if (framebuffer) gl.call("GL30", "glColorMaski", new Class<?>[] {int.class,boolean.class,boolean.class,boolean.class,boolean.class}, 0, colorMask.get(0) != 0, colorMask.get(1) != 0, colorMask.get(2) != 0, colorMask.get(3) != 0);
            else gl.call("GL11", "glColorMask", new Class<?>[] { boolean.class, boolean.class, boolean.class, boolean.class }, colorMask.get(0) != 0, colorMask.get(1) != 0, colorMask.get(2) != 0, colorMask.get(3) != 0);
            if (polygon[0] == polygon[1]) gl.ii("GL11", "glPolygonMode", 1032, polygon[0]);
            else { gl.ii("GL11", "glPolygonMode", 1028, polygon[0]); gl.ii("GL11", "glPolygonMode", 1029, polygon[1]); }
            for (Map.Entry<Integer, Boolean> flag : enabled.entrySet()) {
                if ((framebuffer && flag.getKey() == 3042) || (indexedScissor && flag.getKey() == 3089))
                    gl.ii("GL30", flag.getValue() ? "glEnablei" : "glDisablei", flag.getKey(), 0);
                else gl.i("GL11", flag.getValue() ? "glEnable" : "glDisable", flag.getKey());
            }
            if (clipControl) gl.ii("GL45", "glClipControl", clipOrigin, clipDepthMode);
            if (framebuffer) gl.ii("GL30", "glBindFramebuffer", 36009, 0);
            else if (extFramebuffer) gl.ii("EXTFramebufferObject", "glBindFramebufferEXT", 36160, 0);
            gl.i("GL11", "glDrawBuffer", defaultDrawBuffer);
            if (framebuffer) gl.ii("GL30", "glBindFramebuffer", 36009, drawFbo);
            else if (extFramebuffer) gl.ii("EXTFramebufferObject", "glBindFramebufferEXT", 36160, drawFbo);
        }
    }

    private static final class Gl {
        final ClassLoader loader; final boolean modern;
        final Map<String, Class<?>> classes = new HashMap<String, Class<?>>();
        final Map<String, Method> methods = new HashMap<String, Method>();
        Gl(ClassLoader loader, boolean modern) { this.loader = loader; this.modern = modern; }
        Class<?> type(String name) throws Exception {
            Class<?> type = classes.get(name);
            if (type == null) { type = Class.forName("org.lwjgl.opengl." + name, true, loader); classes.put(name, type); }
            return type;
        }
        Object call(String type, String method, Class<?>[] parameters, Object... values) throws Exception { return call(type(type), method, parameters, values); }
        Object call(Class<?> type, String method, Class<?>[] parameters, Object... values) throws Exception {
            StringBuilder key = new StringBuilder(type.getName()).append('.').append(method);
            for (Class<?> parameter : parameters) key.append(':').append(parameter.getName());
            String name = key.toString();
            Method target = methods.get(name);
            if (target == null) { target = type.getMethod(method, parameters); methods.put(name, target); }
            try { return target.invoke(null, values); }
            catch (java.lang.reflect.InvocationTargetException wrapped) {
                Throwable cause = wrapped.getCause();
                if (cause instanceof Exception) throw (Exception)cause;
                if (cause instanceof Error) throw (Error)cause;
                throw wrapped;
            }
        }
        void i(String type, String method, int a) throws Exception { call(type, method, oneInt(), a); }
        void ii(String type, String method, int a, int b) throws Exception { call(type, method, twoInts(), a, b); }
        int integer(String type, String method) throws Exception { return ((Integer)call(type, method, new Class<?>[0])).intValue(); }
        int get(int parameter) throws Exception { return ((Integer)call("GL11", "glGetInteger", oneInt(), parameter)).intValue(); }
        int[] vector(int parameter, int count) throws Exception {
            IntBuffer data = ints(Math.max(count, 16));
            call("GL11", modern ? "glGetIntegerv" : "glGetInteger", new Class<?>[] { int.class, IntBuffer.class }, parameter, data);
            int[] result = new int[count]; for (int index = 0; index < count; index++) result[index] = data.get(index); return result;
        }
        float[] floatVector(int parameter, int count) throws Exception {
            FloatBuffer data = floats(new float[Math.max(16, count)]);
            call("GL11", modern ? "glGetFloatv" : "glGetFloat", new Class<?>[] {int.class, FloatBuffer.class}, parameter, data);
            float[] result = new float[count]; for (int index = 0; index < count; index++) result[index] = data.get(index); return result;
        }
        Object capabilities() throws Exception { return call(modern ? "GL" : "GLContext", "getCapabilities", new Class<?>[0]); }
        boolean flag(Object capabilities, String flag) {
            try { Field value = capabilities.getClass().getField(flag); return value.getBoolean(capabilities); }
            catch (Exception ignored) { return false; }
        }
    }

    private static Class<?>[] oneInt() { return new Class<?>[] { int.class }; }
    private static Class<?>[] twoInts() { return new Class<?>[] { int.class, int.class }; }
    private static Class<?>[] threeInts() { return new Class<?>[] { int.class, int.class, int.class }; }
    private static Class<?>[] fourInts() { return new Class<?>[] { int.class, int.class, int.class, int.class }; }
    private static IntBuffer ints(int count) { return ByteBuffer.allocateDirect(count * 4).order(ByteOrder.nativeOrder()).asIntBuffer(); }
    private static FloatBuffer floats(float[] data) { FloatBuffer result = ByteBuffer.allocateDirect(data.length * 4).order(ByteOrder.nativeOrder()).asFloatBuffer(); result.put(data).flip(); return result; }
}
