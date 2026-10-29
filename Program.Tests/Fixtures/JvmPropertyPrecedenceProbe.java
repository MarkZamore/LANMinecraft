import java.util.Base64;
import java.nio.charset.StandardCharsets;

public final class JvmPropertyPrecedenceProbe {
    public static void main(String[] args) {
        for (String name : new String[] {"file.encoding", "native.encoding", "sun.jnu.encoding", "stdout.encoding", "stderr.encoding", "java.vendor", "java.runtime.version"})
            System.out.println("PROPERTY_" + name + "=" + System.getProperty(name));
        System.out.println("DEFAULT_CHARSET=" + java.nio.charset.Charset.defaultCharset().name());
        System.out.println("LOGGING=" + System.getProperty("log4j.configurationFile"));
        System.out.println("KEEP=" + System.getProperty("fixture.native.keep"));
        for (int i = 0; i < args.length; i++)
            System.out.println("ARG" + i + "=" + Base64.getEncoder().encodeToString(args[i].getBytes(StandardCharsets.UTF_8)));
    }
}
