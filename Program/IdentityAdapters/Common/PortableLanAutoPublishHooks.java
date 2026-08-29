package minecraft.portable.identity;

import java.util.Collections;
import java.util.Map;
import java.util.WeakHashMap;

public final class PortableLanAutoPublishHooks {
    // One publish attempt per screen instance: Screen.resize() re-runs init() on
    // the same instance and must not re-publish or discard user edits.
    private static final Map<Object, Boolean> ATTEMPTED =
        Collections.synchronizedMap(new WeakHashMap<>());

    private PortableLanAutoPublishHooks() {
    }

    public static boolean autoPublish(Object screen) {
        if (ATTEMPTED.putIfAbsent(screen, Boolean.TRUE) != null) {
            return false;
        }

        try {
            ClassLoader loader = screen.getClass().getClassLoader();
            Class<?> minecraftType = loadClass(
                loader,
                aliases("minecraftClasses", "net.minecraft.client.Minecraft", "fgo"));
            Class<?> screenType = loadClass(
                loader,
                aliases("screenClasses", "net.minecraft.client.gui.screens.Screen", "fod"));
            Class<?> gameTypeType = loadClass(
                loader,
                aliases("gameTypeClasses", "net.minecraft.world.level.GameType", "dct"));
            Class<?> httpUtilType = loadClass(
                loader,
                aliases("httpUtilClasses", "net.minecraft.util.HttpUtil", "ayf"));
            Class<?> publishCommandType = loadClass(
                loader,
                aliases("publishCommandClasses", "net.minecraft.server.commands.PublishCommand", "ans"));
            Class<?> componentType = loadClass(
                loader,
                aliases("componentClasses", "net.minecraft.network.chat.Component", "wz"));
            // An unrelated default-package class can collide with the obfuscated
            // screen name; only a real Screen may trigger a publish.
            if (!screenType.isInstance(screen)) {
                return false;
            }

            Object minecraft = PortableIdentityReflection.invokeStatic(
                minecraftType,
                new Class<?>[0],
                new Object[0],
                aliases("minecraftGetInstanceMethods", "getInstance", "Q"));
            Object server = PortableIdentityReflection.invoke(
                minecraft,
                aliases("getSingleplayerServerMethods", "getSingleplayerServer", "V"));
            if (server == null) {
                return false;
            }
            if (Boolean.TRUE.equals(PortableIdentityReflection.invoke(
                server,
                aliases("isPublishedMethods", "isPublished", "r")))) {
                return false;
            }

            // The values the vanilla screen would pre-select: the world's own
            // game mode and command permission, plus a free port.
            Object gameType = PortableIdentityReflection.invoke(
                server,
                aliases("getDefaultGameTypeMethods", "getDefaultGameType", "u_"));
            Object worldData = PortableIdentityReflection.invoke(
                server,
                aliases("getWorldDataMethods", "getWorldData", "bb"));
            boolean commands = Boolean.TRUE.equals(PortableIdentityReflection.invoke(
                worldData,
                aliases("isAllowCommandsMethods", "isAllowCommands", "m")));
            int port = (Integer) PortableIdentityReflection.invokeStatic(
                httpUtilType,
                new Class<?>[0],
                new Object[0],
                aliases("getAvailablePortMethods", "getAvailablePort", "a"));

            // Publish before closing the screen so every failure path falls
            // through to the untouched vanilla settings UI.
            boolean published = Boolean.TRUE.equals(PortableIdentityReflection.invokeDeclared(
                server,
                new Class<?>[] { gameTypeType, boolean.class, int.class },
                new Object[] { gameType, commands, port },
                aliases("publishServerMethods", "publishServer", "a")));
            if (!published) {
                System.out.println(
                    "[PortableIdentity] LAN auto-publish could not bind port " + port +
                    "; opening the vanilla screen.");
                return false;
            }

            try {
                PortableIdentityReflection.invokeDeclared(
                    minecraft,
                    new Class<?>[] { screenType },
                    new Object[] { null },
                    aliases("setScreenMethods", "setScreen", "a"));
                Object message = PortableIdentityReflection.invokeStatic(
                    publishCommandType,
                    new Class<?>[] { int.class },
                    new Object[] { port },
                    aliases("publishSuccessMethods", "getSuccessMessage", "a"));
                Object gui = PortableIdentityReflection.getField(
                    minecraft,
                    aliases("minecraftGuiFields", "gui", "l"));
                Object chat = PortableIdentityReflection.invoke(
                    gui,
                    aliases("guiChatMethods", "getChat", "d"));
                PortableIdentityReflection.invokeDeclared(
                    chat,
                    new Class<?>[] { componentType },
                    new Object[] { message },
                    aliases("chatAddMessageMethods", "addMessage", "a"));
                PortableIdentityReflection.invoke(
                    minecraft,
                    aliases("updateTitleMethods", "updateTitle", "d"));
            } catch (Throwable exception) {
                // The world is already published; suppressing the guard now
                // would let the vanilla button publish a second time.
                System.out.println(
                    "[PortableIdentity] LAN world published on port " + port +
                    " but screen cleanup failed: " + exception);
            }
            System.out.println("[PortableIdentity] LAN world auto-published on port " + port + ".");
            return true;
        } catch (Throwable exception) {
            System.out.println("[PortableIdentity] LAN auto-publish unavailable: " + exception);
            return false;
        }
    }

    private static Class<?> loadClass(ClassLoader loader, String... names) throws ClassNotFoundException {
        for (String name : names) {
            try {
                return Class.forName(name, true, loader);
            } catch (ClassNotFoundException ignored) {
                // Try the runtime-mapped name.
            }
        }
        throw new ClassNotFoundException(String.join("/", names));
    }

    private static String[] aliases(String propertyName, String... defaults) {
        String value = System.getProperty("minecraft.portable.identity." + propertyName);
        if (value == null || value.isBlank()) {
            return defaults;
        }
        return java.util.Arrays.stream(value.split(","))
            .map(String::trim)
            .filter(candidate -> !candidate.isEmpty())
            .toArray(String[]::new);
    }
}
