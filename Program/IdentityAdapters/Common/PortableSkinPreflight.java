package minecraft.portable.identity;

import java.lang.reflect.Method;

public final class PortableSkinPreflight {
    private PortableSkinPreflight() {
    }

    public static void main(String[] arguments) throws Exception {
        if (arguments.length != 3) {
            throw new IllegalArgumentException("Usage: <registered-url> <unregistered-url> <official-url>");
        }

        Method isAllowed = findDomainCheck();
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
    private static Method findDomainCheck() throws Exception {
        String[] classNames = {
            "com.mojang.authlib.yggdrasil.TextureUrlChecker",
            "com.mojang.authlib.yggdrasil.YggdrasilMinecraftSessionService",
        };
        String[] methodNames = {"isAllowedTextureDomain", "isWhitelistedDomain"};
        for (String className : classNames) {
            Class<?> type;
            try {
                type = Class.forName(className, true, ClassLoader.getSystemClassLoader());
            } catch (ClassNotFoundException absent) {
                continue;
            }
            for (String methodName : methodNames) {
                try {
                    Method method = type.getDeclaredMethod(methodName, String.class);
                    method.setAccessible(true);
                    return method;
                } catch (NoSuchMethodException absent) {
                    // The other form, or the other class, carries it.
                }
            }
        }
        throw new IllegalStateException(
            "This authlib keeps its texture domain rule somewhere new: none of the three known forms is present.");
    }

    private static boolean allowed(Method method, String url) throws Exception {
        return Boolean.TRUE.equals(method.invoke(null, url));
    }
}
