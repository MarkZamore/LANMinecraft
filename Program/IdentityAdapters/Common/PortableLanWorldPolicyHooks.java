package minecraft.portable.identity;

/** Keeps LAN publication from replacing the integrated world's saved settings. */
public final class PortableLanWorldPolicyHooks {
    private static final String PREFIX = "minecraft.portable.identity.";

    private PortableLanWorldPolicyHooks() {
    }

    public static Object publicationGameType(Object server, Object requested) {
        if (requested == null) return null;
        try {
            return PortableIdentityReflection.invoke(server, aliases("getDefaultGameTypeMethods"));
        } catch (ReflectiveOperationException failure) {
            // A null override falls back to the native world's mode.
            System.err.println("[PortableIdentity] LAN game mode override ignored: " + failure);
            return null;
        }
    }

    public static boolean publicationAllowsCommands(Object server, boolean requested) {
        if (!requested) return false;
        try {
            Object worldData = PortableIdentityReflection.invoke(server, aliases("getWorldDataMethods"));
            return worldData != null && Boolean.TRUE.equals(PortableIdentityReflection.invoke(
                worldData, aliases("isAllowCommandsMethods")));
        } catch (ReflectiveOperationException failure) {
            System.err.println("[PortableIdentity] LAN command permission could not be verified: " + failure);
            return false;
        }
    }

    public static boolean playerListAllowsCommands(Object playerList, boolean requested) {
        if (!requested) return false;
        try {
            Object server = PortableIdentityReflection.invoke(playerList, aliases("lanPolicyPlayerListServerMethods"));
            return server != null && (!isIntegratedServer(server) || publicationAllowsCommands(server, true));
        } catch (ReflectiveOperationException failure) {
            System.err.println("[PortableIdentity] LAN player-list command permission could not be verified: " + failure);
            return false;
        }
    }

    private static boolean isIntegratedServer(Object server) {
        String[] types = aliases("integratedServerClasses");
        for (Class<?> type = server.getClass(); type != null; type = type.getSuperclass()) {
            String name = type.getName().replace('.', '/');
            for (String candidate : types) if (candidate.equals(name)) return true;
        }
        return false;
    }

    private static String[] aliases(String property) {
        return System.getProperty(PREFIX + property, "").split(",");
    }
}
