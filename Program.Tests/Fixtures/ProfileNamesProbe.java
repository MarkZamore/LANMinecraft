import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.FileTime;
import java.util.UUID;
import minecraft.portable.identity.PortableIdentityProfiles;
import minecraft.portable.identity.PortableProfileNameTransformer;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;

/** Runs the transformed bytecode with an empty/foreign account name cache. */
public final class ProfileNamesProbe {
    private static long stamp = System.currentTimeMillis();
    private static final UUID OWNER = UUID.fromString("11111111-2222-4333-8444-555555555555");
    private static final UUID STRANGER = UUID.fromString("22222222-2222-4333-8444-555555555555");
    private static Path registry;

    public static void main(String[] args) throws Exception {
        registry = Path.of(args[0]);
        System.setProperty("minecraft.portable.identity.registry", registry.toString());
        write("anuvenn|" + OWNER);
        for (String target : new String[] {
            "snownee/jade/util/CommonProxy",
            "net/neoforged/neoforge/common/UsernameCache",
            "net/minecraftforge/common/UsernameCache"
        }) {
            byte[] original = fixture(target, true, "(Ljava/util/UUID;)Ljava/lang/String;", "original");
            byte[] patched = transform(target, original);
            check(patched != null, "supported lookup not patched");
            Method lookup = new Loader().load(patched).getMethod("getLastKnownUsername", UUID.class);
            check("anuvenn".equals(lookup.invoke(null, OWNER)), "cold cache lost portable owner");
            check("original".equals(lookup.invoke(null, STRANGER)), "foreign lookup changed");
            check("original".equals(lookup.invoke(null, new Object[] {null})), "null lookup changed");
            write("Renamed|" + OWNER);
            check("Renamed".equals(lookup.invoke(null, OWNER)), "running game missed registry refresh");
            check(OWNER.equals(PortableIdentityProfiles.remap(OWNER, "Renamed")), "display lookup changed UUID");
            write("anuvenn|" + OWNER);
            System.out.println("passed: " + target);
        }
        Method nullable = new Loader().load(transform("snownee/jade/util/CommonProxy",
            fixture("snownee/jade/util/CommonProxy", true, "(Ljava/util/UUID;)Ljava/lang/String;", null)))
            .getMethod("getLastKnownUsername", UUID.class);
        check(nullable.invoke(null, STRANGER) == null, "unknown name invented");
        write("First|" + OWNER + "\nSecond|" + OWNER);
        check(nullable.invoke(null, OWNER) == null, "ambiguous UUID guessed");
        write("Same|" + OWNER + "\nSame|" + STRANGER);
        check(nullable.invoke(null, OWNER) == null && nullable.invoke(null, STRANGER) == null,
            "ambiguous account name guessed");
        write("broken|not-a-uuid\nhalf-written\nanuvenn|" + OWNER);
        check("anuvenn".equals(nullable.invoke(null, OWNER)), "malformed line hid valid name");
        Files.delete(registry);
        check(nullable.invoke(null, OWNER) == null, "deleted registry kept stale display name");
        check(transform("example/Other", new byte[0]) == null, "unrelated class touched");
        check(transform("snownee/jade/util/CommonProxy", fixture("snownee/jade/util/CommonProxy",
            false, "(Ljava/util/UUID;)Ljava/lang/String;", null)) == null, "instance method touched");
        check(transform("snownee/jade/util/CommonProxy", fixture("snownee/jade/util/CommonProxy",
            true, "(Ljava/lang/String;)Ljava/lang/String;", null)) == null, "different signature touched");
        System.out.println("Profile name compatibility checks passed.");
    }

    private static byte[] transform(String target, byte[] original) {
        byte[] patched = new PortableProfileNameTransformer().transform(null, null, target, null, null, original);
        // The packaged-agent smoke test supplies original bytes to defineClass,
        // proving that premain registration and the bootstrap hooks work too.
        return patched != null && Boolean.getBoolean("profile.names.agent.probe") ? original : patched;
    }

    private static void write(String contents) throws Exception {
        Files.writeString(registry, contents, StandardCharsets.UTF_8);
        Files.setLastModifiedTime(registry, FileTime.fromMillis(stamp += 2000));
    }

    private static byte[] fixture(String name, boolean isStatic, String descriptor, String fallback) {
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS | ClassWriter.COMPUTE_FRAMES);
        writer.visit(Opcodes.V17, Opcodes.ACC_PUBLIC, name, null, "java/lang/Object", null);
        MethodVisitor method = writer.visitMethod(Opcodes.ACC_PUBLIC | (isStatic ? Opcodes.ACC_STATIC : 0),
            "getLastKnownUsername", descriptor, null, null);
        method.visitCode();
        if (fallback == null) method.visitInsn(Opcodes.ACONST_NULL);
        else method.visitLdcInsn(fallback);
        method.visitInsn(Opcodes.ARETURN);
        method.visitMaxs(0, 0);
        method.visitEnd();
        writer.visitEnd();
        return writer.toByteArray();
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }

    private static final class Loader extends ClassLoader {
        Class<?> load(byte[] data) {
            return defineClass(new ClassReader(data).getClassName().replace('/', '.'), data, 0, data.length);
        }
    }
}
