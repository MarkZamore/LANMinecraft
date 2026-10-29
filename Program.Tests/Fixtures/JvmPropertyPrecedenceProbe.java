import java.util.Base64;
import java.nio.charset.StandardCharsets;

public final class JvmPropertyPrecedenceProbe {
    public static void main(String[] args) {
        System.out.println("LOGGING=" + System.getProperty("log4j.configurationFile"));
        System.out.println("KEEP=" + System.getProperty("fixture.native.keep"));
        for (int i = 0; i < args.length; i++)
            System.out.println("ARG" + i + "=" + Base64.getEncoder().encodeToString(args[i].getBytes(StandardCharsets.UTF_8)));
    }
}
