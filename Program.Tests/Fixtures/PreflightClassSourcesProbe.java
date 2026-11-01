import java.nio.file.Files;
import java.nio.file.Path;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;
import minecraft.portable.identity.PortableIdentityPreflight;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;

/** Reproduces Forge's patched-target/sibling-support split without loading game classes. */
public final class PreflightClassSourcesProbe {
    private static final String PREFIX = "minecraft.portable.identity.";
    private static final String SCREEN = "fixture/JoinScreen";
    private static final String DATA = "fixture/ServerData";
    private static final String LIST = "fixture/LanList";
    private static final String LAN = "fixture/LanServer";
    private static final String MINECRAFT = "fixture/Minecraft";
    private static final String USER = "fixture/User";
    private static final String WORLD = "fixture/WorldData";
    private static final String COMPONENT = "fixture/Component";
    public static void main(String[] args) throws Exception {
        Path root = Path.of(args[0]);
        for (String name : new String[] { "ftbTeleportClasses", "solarFluxPackClasses", "xaeroWaypointTeleportClasses",
                "lanShareScreenClasses", "loginClasses", "playerInfoClasses", "textureUrlCheckerClasses", "gameProfileClasses" })
            set(name, "unused/None");
        Files.createDirectories(root);
        Path target = root.resolve("patched-client.jar"), support = root.resolve("native-srg.jar"), bad = root.resolve("wrong-srg.jar");
        try (ZipOutputStream zip = new ZipOutputStream(Files.newOutputStream(target))) {
            ClassWriter writer = type(SCREEN);
            writer.visitField(Opcodes.ACC_PUBLIC, "lan", "L"+LIST+";", null, null).visitEnd();
            method(writer, "init", "()V"); method(writer, "removed", "()V"); method(writer, "join", "(L"+DATA+";)V");
            entry(zip, SCREEN, writer);
        }
        for (Path path : new Path[] { support, bad }) try (ZipOutputStream zip = new ZipOutputStream(Files.newOutputStream(path))) {
            ClassWriter data = type(DATA); data.visitField(Opcodes.ACC_PUBLIC, "ip", "Ljava/lang/String;", null, null).visitEnd(); entry(zip, DATA, data);
            ClassWriter list = type(LIST); list.visitField(Opcodes.ACC_PUBLIC, "servers", "Ljava/util/List;", null, null).visitEnd();
            list.visitField(Opcodes.ACC_PUBLIC, "dirty", "Z", null, null).visitEnd();
            method(list, "addServer", "(Ljava/lang/String;Ljava/net/InetAddress;)V"); entry(zip, LIST, list);
            ClassWriter lan = type(LAN); method(lan, "<init>", "(Ljava/lang/String;Ljava/lang/String;)V");
            if (path.equals(support)) method(lan, "getAddress", "()Ljava/lang/String;");
            entry(zip, LAN, lan);
            ClassWriter minecraft = type(MINECRAFT); method(minecraft, "getUser", "()L"+USER+";"); entry(zip, MINECRAFT, minecraft);
            ClassWriter user = type(USER); method(user, "getName", "()Ljava/lang/String;"); entry(zip, USER, user);
            ClassWriter world = type(WORLD); method(world, "getLevelName", "()Ljava/lang/String;"); entry(zip, WORLD, world);
            ClassWriter component = type(COMPONENT);
            method(component, Opcodes.ACC_PUBLIC | Opcodes.ACC_STATIC, "literal", "(Ljava/lang/String;)L"+COMPONENT+";");
            entry(zip, COMPONENT, component);
        }
        set("steamDiscoveryEnabled", "true"); set("steamDiscoveryScreenClasses", SCREEN);
        set("steamDiscoveryInitMethods", "init"); set("steamDiscoveryRemovedMethods", "removed");
        set("steamDiscoveryJoinMethods", "join"); set("steamDiscoveryJoinDescriptors", "(L"+DATA+";)V");
        set("steamDiscoveryLanListFields", "lan"); set("steamDiscoveryLanServersFields", "servers");
        set("steamDiscoveryLanDirtyFields", "dirty"); set("steamDiscoveryLanListClasses", LIST);
        set("steamDiscoveryLanAddMethods", "addServer");
        set("steamDiscoveryLanAddDescriptors", "(Ljava/lang/String;Ljava/net/InetAddress;)V");
        set("steamDiscoveryServerDataClasses", DATA); set("steamDiscoveryServerAddressFields", "ip");
        set("steamDiscoveryLanServerClasses", LAN); set("steamDiscoveryLanAddressMethods", "getAddress");
        set("minecraftClasses", MINECRAFT); set("steamDiscoveryGetUserMethods", "getUser");
        set("steamDiscoveryUserClasses", USER); set("steamDiscoveryUserNameMethods", "getName");
        set("steamDiscoveryWorldDataClasses", WORLD); set("steamDiscoveryLevelNameMethods", "getLevelName");
        set("steamDiscoveryComponentClasses", COMPONENT); set("steamDiscoveryComponentLiteralMethods", "literal");
        refused("missing sibling source", new String[] {target.toString(), SCREEN}, "Required identity class is missing");
        PortableIdentityPreflight.main(input(target, support));
        for (String missing : new String[] { MINECRAFT, USER, WORLD, COMPONENT })
            refused("missing display source "+missing, java.util.Arrays.stream(input(target, support))
                .filter(value -> !value.startsWith(missing+"=")).toArray(String[]::new), "Required identity class is missing: "+missing);
        refused("wrong member in supplied source", input(target, bad), "Required identity method is missing");
        String[] duplicate = {target.toString(), SCREEN, DATA+"="+support, DATA+"="+bad};
        refused("ambiguous duplicate source", duplicate, "Duplicate native preflight class source");
        refused("target shadow", new String[] {target.toString(), SCREEN, SCREEN+"="+support}, "target source differs");
        System.out.println("PASS split native preflight: explicit sibling sources, missing/wrong/duplicate/shadow sources refused.");
    }
    private static String[] input(Path target, Path support) { return new String[] {target.toString(), SCREEN,
        DATA+"="+support, LIST+"="+support, LAN+"="+support, MINECRAFT+"="+support,
        USER+"="+support, WORLD+"="+support, COMPONENT+"="+support}; }
    private static void set(String name, String value) { System.setProperty(PREFIX+name, value); }
    private static void refused(String label, String[] args, String expected) throws Exception {
        try { PortableIdentityPreflight.main(args); }
        catch (IllegalStateException | IllegalArgumentException exception) {
            if (exception.getMessage().contains(expected)) return;
            throw new AssertionError(label+": unexpected refusal", exception);
        }
        throw new AssertionError(label+": unexpectedly accepted");
    }
    private static ClassWriter type(String name) {
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS); writer.visit(61, Opcodes.ACC_PUBLIC, name, null, "java/lang/Object", null); return writer;
    }
    private static void method(ClassWriter writer, String name, String descriptor) {
        method(writer, Opcodes.ACC_PUBLIC, name, descriptor);
    }
    private static void method(ClassWriter writer, int access, String name, String descriptor) {
        MethodVisitor method = writer.visitMethod(access, name, descriptor, null, null); method.visitCode();
        if (name.equals("<init>")) { method.visitVarInsn(Opcodes.ALOAD, 0); method.visitMethodInsn(Opcodes.INVOKESPECIAL, "java/lang/Object", "<init>", "()V", false); }
        if (descriptor.endsWith("Ljava/lang/String;")) { method.visitLdcInsn("address"); method.visitInsn(Opcodes.ARETURN); }
        else if (Type.getReturnType(descriptor).getSort() == Type.OBJECT) { method.visitInsn(Opcodes.ACONST_NULL); method.visitInsn(Opcodes.ARETURN); }
        else method.visitInsn(Opcodes.RETURN);
        method.visitMaxs(0, 0); method.visitEnd();
    }
    private static void entry(ZipOutputStream zip, String name, ClassWriter writer) throws Exception {
        writer.visitEnd(); zip.putNextEntry(new ZipEntry(name+".class")); zip.write(writer.toByteArray()); zip.closeEntry();
    }
}
