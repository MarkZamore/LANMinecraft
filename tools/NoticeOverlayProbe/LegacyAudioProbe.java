import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import org.lwjgl.openal.AL;
import org.lwjgl.openal.AL10;

/** Loads the actual LWJGL 2 OpenAL binary without a display or audible output. */
public final class LegacyAudioProbe {
    public static void main(String[] args) throws Exception {
        try {
            AL.create();
            System.out.println("OpenAL vendor: " + AL10.alGetString(AL10.AL_VENDOR));
            System.out.println("OpenAL renderer: " + AL10.alGetString(AL10.AL_RENDERER));
            int buffer = AL10.alGenBuffers(), source = AL10.alGenSources();
            try {
                ByteBuffer silence = ByteBuffer.allocateDirect(44100 * 2).order(ByteOrder.nativeOrder());
                AL10.alBufferData(buffer, AL10.AL_FORMAT_MONO16, silence, 44100);
                AL10.alSourcei(source, AL10.AL_BUFFER, buffer);
                AL10.alSourcef(source, AL10.AL_GAIN, 0);
                AL10.alSourcePlay(source);
                Thread.sleep(150);
                int state = AL10.alGetSourcei(source, AL10.AL_SOURCE_STATE);
                float offset = AL10.alGetSourcef(source, 0x1024); // AL_SEC_OFFSET
                int error = AL10.alGetError();
                if (state != AL10.AL_PLAYING || offset <= 0 || error != AL10.AL_NO_ERROR)
                    throw new AssertionError("Audio source did not advance: " + state + ", " + offset + ", " + error);
                System.out.println("PASS: silent PCM source advances, seconds=" + offset);
            } finally {
                AL10.alDeleteSources(source);
                AL10.alDeleteBuffers(buffer);
            }
        } finally { AL.destroy(); }
    }
}
