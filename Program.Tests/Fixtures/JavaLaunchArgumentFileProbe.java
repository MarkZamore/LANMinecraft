import java.nio.charset.StandardCharsets;
import java.util.Base64;

public final class JavaLaunchArgumentFileProbe {
    public static void main(String[] args) {
        System.out.println("LENGTH=" + System.getProperty("fixture.long", "").length());
        System.out.println("VALUE=" + encode(System.getProperty("fixture.value")));
        for (int index = 0; index < args.length; index++) {
            System.out.println("ARG" + index + "=" + encode(args[index]));
        }
    }

    private static String encode(String value) {
        return Base64.getEncoder().encodeToString(value.getBytes(StandardCharsets.UTF_8));
    }
}
