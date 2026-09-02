package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;

/**
 * Registers an Applied Energistics menu once instead of twice.
 *
 * <p>AE2 offers two builders. {@code buildUnregistered(id)} makes a MenuType.
 * {@code build(id)} makes it and also hands the id to {@code InitMenuTypes} for
 * AE2 to register. Called from inside a NeoForge {@code DeferredRegister}
 * supplier, the second one is a double registration: AE2 registers the id, then
 * NeoForge registers the same id, and whichever runs second throws
 * {@code Adding duplicate key}. From there the whole registry rolls back to
 * vanilla, whatever is rebuilding blocks during the rollback dies with it, every
 * mod is marked broken, and the crash report names whoever happened to touch a
 * config on the error screen the game could no longer draw.
 *
 * <p>Two mods in this pack do it: ae2addonlib, which Advanced AE carries and
 * only UFO FUTURE uses, and UFO FUTURE's own menu registry. Rewriting the call
 * to {@code buildUnregistered} leaves NeoForge as the only registrar, which is
 * what the DeferredRegister was there for. The descriptor is identical, so the
 * rewrite is the method name and nothing else.
 *
 * <p>Doing it here rather than in the jars is what keeps the pack shipping the
 * mods as their authors published them, and lets them update without the patch
 * having to be made again.
 */
public final class PortableMenuRegistrationTransformer implements ClassFileTransformer {
    private static final String DEFAULT_TARGETS =
        "net/pedroksl/ae2addonlib/registry/MenuRegistry,com/raishxn/ufo/init/ModMenus";
    private static final String DEFAULT_BUILDER =
        "appeng/menu/implementations/MenuTypeBuilder";
    private static final String DEFAULT_REGISTERING = "build";
    private static final String DEFAULT_UNREGISTERED = "buildUnregistered";
    private static final String DEFAULT_DESCRIPTOR =
        "(Lnet/minecraft/resources/ResourceLocation;)Lnet/minecraft/world/inventory/MenuType;";

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        if (!Boolean.getBoolean("minecraft.portable.identity.menuRegistrationFixEnabled") ||
            !contains(property("menuRegistrationFixClasses", DEFAULT_TARGETS), className)) {
            return null;
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);

        String owner = property("menuBuilderClasses", DEFAULT_BUILDER);
        String registering = property("menuBuilderRegisteringMethods", DEFAULT_REGISTERING);
        String unregistered = property("menuBuilderUnregisteredMethods", DEFAULT_UNREGISTERED);
        String descriptors = property("menuBuilderDescriptors", DEFAULT_DESCRIPTOR);

        int rewritten = 0;
        for (MethodNode method : node.methods) {
            for (AbstractInsnNode instruction : method.instructions) {
                if (!(instruction instanceof MethodInsnNode call) ||
                    !contains(owner, call.owner) ||
                    !contains(registering, call.name) ||
                    !contains(descriptors, call.desc)) {
                    continue;
                }
                call.name = first(unregistered);
                rewritten++;
            }
        }

        // None is the good ending, not a failure: it is what a version of the
        // mod that stopped doing this looks like, and what this class looks
        // like when the pack is still shipping a patched jar of its own. Saying
        // nothing at all would leave no way to tell that from a transformer
        // that quietly stopped matching, so it is said either way.
        if (rewritten == 0) {
            System.out.println(
                "[PortableIdentity] " + className +
                " registers its menus once already; nothing to rewrite.");
            return null;
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println(
            "[PortableIdentity] Patched " + className + ": " + rewritten +
            " menu registration(s) now go through NeoForge only.");
        return writer.toByteArray();
    }

    private static String property(String name, String fallback) {
        String value = System.getProperty("minecraft.portable.identity." + name);
        return value == null || value.isBlank() ? fallback : value;
    }

    private static String first(String csv) {
        String[] candidates = csv.split(",");
        return candidates.length == 0 ? csv.trim() : candidates[0].trim();
    }

    private static boolean contains(String csv, String value) {
        for (String candidate : csv.split(",")) {
            if (candidate.trim().equals(value)) {
                return true;
            }
        }
        return false;
    }
}
