import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.stream.Stream;

/** Build-only relocation; this class is never packaged in the runtime agent. */
public final class RelocateAsm {
    private static final String ORIGINAL = "org/objectweb/asm";
    private static final String PRIVATE = "minecraft/portable/internal/asm";

    private RelocateAsm() { }

    public static void main(String[] args) throws IOException {
        if (args.length < 2 || args.length > 3) throw new IllegalArgumentException("Expected compiled agent, extracted ASM directories and optional private package");
        String privatePackage = args.length == 3 ? args[2] : PRIVATE;
        if (!privatePackage.startsWith("minecraft/portable/") || !privatePackage.matches("[a-z][a-z0-9_]*(/[a-z][a-z0-9_]*)+"))
            throw new IllegalArgumentException("Invalid private agent package");
        Path classes = Path.of(args[0]).toRealPath();
        Path asm = Path.of(args[1]).toRealPath();
        if (classes.equals(asm) || classes.startsWith(asm) || asm.startsWith(classes))
            throw new IllegalArgumentException("Relocation inputs must be independent staging directories");
        rewriteTree(classes, false, privatePackage);
        rewriteTree(asm, true, privatePackage);
    }

    private static void rewriteTree(Path root, boolean relocatePaths, String privatePackage) throws IOException {
        List<Path> files;
        try (Stream<Path> tree = Files.walk(root)) {
            files = tree.filter(path -> path.toString().endsWith(".class")).sorted().toList();
        }
        if (files.isEmpty()) throw new IOException("No compiled classes under " + root);
        for (Path source : files) {
            if (!source.normalize().startsWith(root) || Files.isSymbolicLink(source))
                throw new IOException("Linked or escaping staged class");
            String relative = root.relativize(source).toString().replace('\\', '/');
            if (relocatePaths && !relative.startsWith(ORIGINAL + "/"))
                throw new IOException("Unexpected extracted ASM entry: " + relative);
            Path target = root.resolve(relocatePaths ? privatePackage + relative.substring(ORIGINAL.length()) : relative).normalize();
            if (!target.startsWith(root)) throw new IOException("Relocation target escapes staging");
            byte[] rewritten = rewrite(Files.readAllBytes(source), privatePackage);
            Files.createDirectories(target.getParent());
            Files.write(target, rewritten);
            if (!source.equals(target)) Files.delete(source);
        }
        if (relocatePaths) {
            List<Path> directories;
            try (Stream<Path> tree = Files.walk(root)) {
                directories = tree.filter(Files::isDirectory)
                        .sorted((a, b) -> Integer.compare(b.getNameCount(), a.getNameCount())).toList();
            }
            for (Path directory : directories) {
                if (directory.equals(root) || !directory.normalize().startsWith(root)) continue;
                try (var children = Files.newDirectoryStream(directory)) {
                    if (children.iterator().hasNext()) continue;
                }
                Files.delete(directory);
            }
        }
    }

    /**
     * Class references, descriptors, signatures and reflective names all live
     * in UTF8 constants. Preserve every other byte and the pool indices, so
     * frames, handles and invokedynamic sites need no interpretation/remapping.
     * readUTF/writeUTF deliberately use the class file's modified UTF-8 format.
     */
    static byte[] rewrite(byte[] original, String privatePackage) throws IOException {
        var result = new ByteArrayOutputStream(original.length);
        try (var input = new DataInputStream(new ByteArrayInputStream(original));
             var output = new DataOutputStream(result)) {
            int magic = input.readInt();
            if (magic != 0xcafebabe) throw new IOException("Invalid staged class file");
            output.writeInt(magic);
            output.writeShort(input.readUnsignedShort());
            output.writeShort(input.readUnsignedShort());
            int count = input.readUnsignedShort();
            output.writeShort(count);
            for (int index = 1; index < count; index++) {
                int tag = input.readUnsignedByte();
                output.writeByte(tag);
                switch (tag) {
                    case 1 -> output.writeUTF(input.readUTF()
                            .replace(ORIGINAL, privatePackage)
                            .replace("org.objectweb.asm", privatePackage.replace('/', '.')));
                    case 3, 4, 9, 10, 11, 12, 17, 18 -> output.writeInt(input.readInt());
                    case 5, 6 -> { output.writeLong(input.readLong()); index++; }
                    case 7, 8, 16, 19, 20 -> output.writeShort(input.readUnsignedShort());
                    case 15 -> { output.writeByte(input.readUnsignedByte()); output.writeShort(input.readUnsignedShort()); }
                    default -> throw new IOException("Unknown constant pool tag: " + tag);
                }
            }
            input.transferTo(output);
        }
        return result.toByteArray();
    }
}
