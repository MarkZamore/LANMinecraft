import java.nio.file.*;
import java.util.*;
import java.util.zip.*;
import org.objectweb.asm.*;
import org.objectweb.asm.tree.*;
import org.objectweb.asm.tree.analysis.*;
import lanmc.experiment.ExperimentalWarningAgent;

/** Analyzes exact original and transformed Mojang methods without loading a world. */
public final class NativeBytecodeProbe {
    public static void main(String[] arguments) throws Exception {
        for (String path : arguments) {
            int classes = 0;
            int methods = 0;
            try (ZipFile archive = new ZipFile(path)) {
                Enumeration<? extends ZipEntry> entries = archive.entries();
                while (entries.hasMoreElements()) {
                    ZipEntry entry = entries.nextElement();
                    if (!entry.getName().endsWith(".class")) continue;
                    byte[] original = archive.getInputStream(entry).readAllBytes();
                    byte[] changed = ExperimentalWarningAgent.patch(original);
                    if (changed == null) continue;
                    ClassNode before = new ClassNode();
                    ClassNode after = new ClassNode();
                    new ClassReader(original).accept(before, 0);
                    new ClassReader(changed).accept(after, 0);
                    if (before.fields.size() != after.fields.size() || before.methods.size() != after.methods.size())
                        throw new AssertionError("Class structure changed " + entry.getName());
                    for (MethodNode method : after.methods) {
                        if ((method.access & (Opcodes.ACC_ABSTRACT | Opcodes.ACC_NATIVE)) != 0) continue;
                        new Analyzer<BasicValue>(new BasicVerifier()).analyze(after.name, method);
                        methods++;
                    }
                    classes++;
                }
            }
            if (classes == 0) throw new AssertionError("Prompt missing " + path);
            System.out.println(path + " PASS classes=" + classes + " analyzedMethods=" + methods);
        }
    }
}
