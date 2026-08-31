package minecraft.portable.identity;

import java.util.concurrent.atomic.AtomicBoolean;

/**
 * The guest is trusted with where he says he is, the way the host already is.
 *
 * Minecraft checks every movement packet against how far a player could have
 * got since the last one, and a step too long is refused and the player put
 * back where he was. On a public server that is the check that stops a flying
 * cheat. Here there is no public server and no cheat: there is one friend on a
 * Steam relay, and half a second of his packets not arriving is enough to make
 * the next one look like a leap. He is then thrown backwards for it, which is
 * the rubber-banding a guest complains about and which no amount of chunk
 * pacing could ever fix - it is not the ground arriving late, it is his own
 * position being rejected.
 *
 * The game already exempts one player from this: whoever opened the world.
 * That exemption exists for exactly this reason, and it stops at the host only
 * because vanilla has no notion of a world opened to two friends rather than to
 * the internet. So the exemption is widened to everybody in such a world, which
 * is what this returns.
 *
 * What it does NOT widen: the same question is asked elsewhere in the same
 * class about who may change the world's difficulty and about ending the
 * session, and neither is any of a guest's business. Only the two movement
 * handlers are rewritten, which is why this is a hook the transformer places by
 * name rather than a value the whole class is made to answer.
 */
public final class PortableGuestMovementHooks {
    private PortableGuestMovementHooks() {
    }

    private static final AtomicBoolean SAID = new AtomicBoolean();

    /**
     * Called where the game asked whether this connection belongs to the player
     * who opened the world. The connection is taken and ignored: it is on the
     * stack because the question was asked of it, and taking it here is what
     * leaves the stack as the game left it.
     */
    public static boolean trustedLikeTheHost(Object listener) {
        if (SAID.compareAndSet(false, true)) {
            System.out.println(
                "[PortableIdentity] guests are trusted with their own movement, as the host already is.");
        }
        return true;
    }
}
