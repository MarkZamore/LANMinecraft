package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.FieldInsnNode;
import org.objectweb.asm.tree.FrameNode;
import org.objectweb.asm.tree.InsnList;
import org.objectweb.asm.tree.InsnNode;
import org.objectweb.asm.tree.JumpInsnNode;
import org.objectweb.asm.tree.LabelNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;
import org.objectweb.asm.tree.VarInsnNode;

/**
 * Gives each guest the chunks he asked for, on a Minecraft that has one number
 * for everybody.
 *
 * What is rewritten is the reading of that number, not any decision made from
 * it. ChunkMap.updatePlayerStatus and ChunkMap.move are both handed the player
 * they are working on and then read this.viewDistance several times, to work
 * out which chunks he should have now and which he had before; each of those
 * reads becomes "the smaller of the server's number and what this player asked
 * for". The game's own arithmetic is untouched, which is the whole point: the
 * two answers stay consistent with one another, so a chunk that is no longer
 * his leaves through the same branch that unloads one he walked away from, and
 * nothing is sent that nobody asked for.
 *
 * The other patch is the one that knows what he asked for: the number arrives
 * in ServerPlayer.updateOptions, and the server does not keep it before 1.20.2.
 *
 * Installed only where the game cannot do this itself - the launcher works that
 * out from the mappings and says so in the flag.
 */
public final class PortablePerPlayerChunksTransformer implements ClassFileTransformer {
    private static final String HOOKS =
        "minecraft/portable/identity/PortablePerPlayerChunksHooks";

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        if (!"true".equals(property("perPlayerChunksEnabled", "false"))) {
            return null;
        }
        boolean chunkMap = contains(property("chunkMapClasses", ""), className);
        boolean serverPlayer = contains(property("serverPlayerClasses", ""), className);
        boolean trackedEntity = contains(property("trackedEntityClasses", ""), className);
        if (!chunkMap && !serverPlayer && !trackedEntity) {
            return null;
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);

        int patched = 0;
        for (MethodNode method : node.methods) {
            // Name and descriptor both. A name alone does not say which method:
            // an obfuscated ServerPlayer has twenty-two one-argument void
            // methods called "a", and patching all of them would fail the
            // preflight and take the whole adapter down with it.
            if (chunkMap && (matches(method, "updatePlayerStatusMethods", "updatePlayerStatusDescriptors") ||
                matches(method, "movePlayerMethods", "movePlayerDescriptors"))) {
                patched += askThePlayerInstead(node.name, method);
            } else if (trackedEntity && matches(method, "updatePlayerMethods", "updatePlayerDescriptors")) {
                // An entity is tracked as far as it carries, capped by how far
                // the world is served. The cap is the same number, reached
                // through the map's own instance from the inner class, so it
                // narrows the same way - otherwise a guest is told about mobs
                // standing where he has no ground.
                patched += askThePlayerInstead(property("chunkMapClasses", ""), method);
            } else if (chunkMap && isDelivery(method)) {
                onlyIfHeAskedForIt(method);
                patched++;
            } else if (serverPlayer && matches(method, "updateOptionsMethods", "updateOptionsDescriptors")) {
                rememberWhatWasAsked(method);
                patched++;
            }
        }
        if (patched == 0) {
            return null;
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        return writer.toByteArray();
    }

    /**
     * Every "this.viewDistance" in the method becomes "what this player should
     * have", asked with the player the method was given.
     *
     * The read it replaces is always "aload_0, getfield" - the field is on the
     * ChunkMap and there is only one ChunkMap in scope - and what goes in its
     * place leaves the stack as it found it: the object that was about to be
     * read from is consumed by the call instead, and an int comes back. No
     * local is touched and no branch is added, so nothing about the method's
     * frames changes.
     */
    private static int askThePlayerInstead(String owners, MethodNode method) {
        String fields = property("chunkViewDistanceFields", "viewDistance");
        int replaced = 0;
        AbstractInsnNode instruction = method.instructions.getFirst();
        while (instruction != null) {
            AbstractInsnNode next = instruction.getNext();
            if (instruction instanceof FieldInsnNode read &&
                read.getOpcode() == Opcodes.GETFIELD &&
                contains(owners, read.owner) &&
                contains(fields, read.name) &&
                // What is on the stack has to be the map itself: "this" in the
                // map's own methods, and the outer instance where an inner
                // class reaches out to it. Read from anything else and the
                // player is not its subject, so it is left alone.
                readsTheMap(read.getPrevious(), owners)) {
                InsnList replacement = new InsnList();
                replacement.add(new VarInsnNode(Opcodes.ALOAD, 1));
                replacement.add(new MethodInsnNode(
                    Opcodes.INVOKESTATIC,
                    HOOKS,
                    "radiusFor",
                    "(Ljava/lang/Object;Ljava/lang/Object;)I",
                    false));
                method.instructions.insertBefore(read, replacement);
                method.instructions.remove(read);
                replaced++;
            }
            instruction = next;
        }
        return replaced;
    }

    /**
     * Whether the instruction before a read put the chunk map on the stack:
     * "this", or the field an inner class holds its outer instance in.
     */
    private static boolean readsTheMap(AbstractInsnNode before, String owners) {
        if (before instanceof VarInsnNode load) {
            return load.getOpcode() == Opcodes.ALOAD && load.var == 0;
        }
        if (before instanceof FieldInsnNode outer && outer.getOpcode() == Opcodes.GETFIELD) {
            for (String owner : owners.split(",")) {
                if (outer.desc.equals("L" + owner.trim() + ";")) {
                    return true;
                }
            }
        }
        return false;
    }

    /**
     * The delivery of one chunk to one player, which is named by the player it
     * starts with and the chunk it ends with rather than by a whole descriptor:
     * what sits between them was an array of packets until 1.18 and a
     * MutableObject after it, and neither is anything to do with this.
     */
    private static boolean isDelivery(MethodNode method) {
        if (!contains(property("playerLoadedChunkMethods", ""), method.name)) {
            return false;
        }
        for (String player : property("serverPlayerClasses", "").split(",")) {
            for (String chunk : property("levelChunkClasses", "").split(",")) {
                if (method.desc.startsWith("(L" + player.trim() + ";") &&
                    method.desc.endsWith("L" + chunk.trim() + ";)V")) {
                    return true;
                }
            }
        }
        return false;
    }

    /**
     * "if he did not ask for this chunk, do not hand it to him" in front of the
     * handing over.
     *
     * This is the one path that does not go through a method holding the player
     * it is working on: a chunk that has finished loading is offered to
     * everybody within the server's radius, and there the player is a loop
     * variable, so the number cannot be narrowed where it is read. It is
     * narrowed here instead, at the delivery, where the player is the first
     * argument and the chunk the third.
     */
    private static void onlyIfHeAskedForIt(MethodNode method) {
        LabelNode hand = new LabelNode();
        InsnList head = new InsnList();
        head.add(new VarInsnNode(Opcodes.ALOAD, 0));
        head.add(new VarInsnNode(Opcodes.ALOAD, 1));
        head.add(new VarInsnNode(Opcodes.ALOAD, 3));
        head.add(new MethodInsnNode(
            Opcodes.INVOKESTATIC,
            HOOKS,
            "shouldSend",
            "(Ljava/lang/Object;Ljava/lang/Object;Ljava/lang/Object;)Z",
            false));
        head.add(new JumpInsnNode(Opcodes.IFNE, hand));
        head.add(new InsnNode(Opcodes.RETURN));
        head.add(hand);
        // Nothing on the stack and the locals the method was entered with,
        // which is what F_SAME says. Without it the class carries a jump to a
        // label no frame describes and will not verify.
        head.add(new FrameNode(Opcodes.F_SAME, 0, null, 0, null));
        method.instructions.insert(head);
    }

    /** "remember what this player asked for", in front of the reading of it. */
    private static void rememberWhatWasAsked(MethodNode method) {
        InsnList head = new InsnList();
        head.add(new VarInsnNode(Opcodes.ALOAD, 0));
        head.add(new VarInsnNode(Opcodes.ALOAD, 1));
        head.add(new MethodInsnNode(
            Opcodes.INVOKESTATIC,
            HOOKS,
            "observeOptions",
            "(Ljava/lang/Object;Ljava/lang/Object;)V",
            false));
        method.instructions.insert(head);
    }

    private static boolean matches(MethodNode method, String nameProperty, String descriptorProperty) {
        return contains(property(nameProperty, ""), method.name) &&
            contains(property(descriptorProperty, ""), method.desc);
    }

    private static String property(String name, String fallback) {
        String value = System.getProperty("minecraft.portable.identity." + name);
        return value == null || value.isBlank() ? fallback : value;
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
