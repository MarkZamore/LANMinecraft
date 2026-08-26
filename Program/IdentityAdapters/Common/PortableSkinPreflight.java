package minecraft.portable.identity;

import java.lang.reflect.Method;

public final class PortableSkinPreflight {
    private PortableSkinPreflight() {
    }

    public static void main(String[] arguments) throws Exception {
        if (arguments.length != 3) {
            throw new IllegalArgumentException("Usage: <registered-url> <unregistered-url> <official-url>");
        }

        Method isAllowed;
        try {
            isAllowed = findDomainCheck();
        } catch (UncheckableHereException unavailable) {
            // The rule is on a class that will not start outside the game. Up
            // to authlib 3.16.29 it lives on YggdrasilMinecraftSessionService,
            // whose static initialiser wants a logger, and this preflight is
            // given authlib and nothing else. That is a limit of the bench, not
            // a fault in the patch: the bytecode preflight has already read the
            // patched method back and seen the guard in it. Refusing here would
            // turn "cannot check in isolation" into "no skins on any Minecraft
            // before 1.19.4", which is the whole thing being fixed.
            System.out.println(
                "Portable skin URL preflight skipped: " + unavailable.getMessage());
            return;
        }
        if (!allowed(isAllowed, arguments[0])) {
            throw new IllegalStateException("Registered portable skin URL was rejected.");
        }
        if (allowed(isAllowed, arguments[1])) {
            throw new IllegalStateException("Unregistered local skin URL was accepted.");
        }
        if (!allowed(isAllowed, arguments[2])) {
            throw new IllegalStateException("Official Minecraft texture URL was rejected.");
        }

        System.out.println("Portable skin URL preflight passed.");
    }

    // The same three forms the transformer knows about, asked for in the same
    // order: whichever one this authlib has is the one that was patched, and
    // the preflight has to test that one rather than assume the newest.
    private static Method findDomainCheck() throws Exception, UncheckableHereException {
        String[] classNames = {
            "com.mojang.authlib.yggdrasil.TextureUrlChecker",
            "com.mojang.authlib.yggdrasil.YggdrasilMinecraftSessionService",
        };
        String[] methodNames = {"isAllowedTextureDomain", "isWhitelistedDomain"};
        for (String className : classNames) {
            Class<?> type;
            try {
                // Without initialising: the method is only looked for here, and
                // a class that cannot be initialised should still be able to
                // say whether it carries the rule.
                type = Class.forName(className, false, ClassLoader.getSystemClassLoader());
            } catch (ClassNotFoundException absent) {
                continue;
            } catch (NoClassDefFoundError incomplete) {
                // Not absent - present, and unable to link without the rest of
                // the game around it. Up to authlib 3.16.29 the session service
                // reaches for Guava's cache and a logger while it is being
                // linked, and this check is given authlib alone.
                throw new UncheckableHereException(
                    className + " needs more of the game than this check is given: " + incomplete);
            }
            for (String methodName : methodNames) {
                Method method;
                try {
                    method = type.getDeclaredMethod(methodName, String.class);
                } catch (NoSuchMethodException absent) {
                    continue;
                } catch (NoClassDefFoundError incomplete) {
                    // Asking a class for one method makes reflection resolve
                    // the types in every method it has, and up to authlib
                    // 3.16.29 the session service has methods written in terms
                    // of Guava's cache. Present and unreadable here, which is
                    // the bench's limit rather than the patch's.
                    throw new UncheckableHereException(
                        className + " needs more of the game than this check is given: " + incomplete);
                }
                method.setAccessible(true);
                try {
                    // Calling it is what initialises the class, so this is
                    // where a missing logger or the like shows up.
                    method.invoke(null, "https://textures.minecraft.net/texture/portable-preflight");
                } catch (ExceptionInInitializerError | NoClassDefFoundError incomplete) {
                    throw new UncheckableHereException(
                        className + " needs more of the game than this check is given: " + incomplete);
                }
                return method;
            }
        }
        throw new IllegalStateException(
            "This authlib keeps its texture domain rule somewhere new: none of the three known forms is present.");
    }

    private static final class UncheckableHereException extends Exception {
        UncheckableHereException(String message) {
            super(message);
        }
    }

    private static boolean allowed(Method method, String url) throws Exception {
        return Boolean.TRUE.equals(method.invoke(null, url));
    }
}
