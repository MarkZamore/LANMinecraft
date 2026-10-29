package minecraft.portable.identity;

import java.util.Collections;
import java.util.Map;
import java.util.WeakHashMap;

public final class PortableLanAutoPublishHooks {
    // One publish attempt per screen instance: Screen.resize() re-runs init() on
    // the same instance and must not re-publish or discard user edits.
    private static final Map<Object, Boolean> ATTEMPTED =
        Collections.synchronizedMap(new WeakHashMap<>());
    private static final Map<Object, Boolean> REQUESTED =
        Collections.synchronizedMap(new WeakHashMap<>());

    private PortableLanAutoPublishHooks() {
    }

    /** Adds the explicit LAN action beside the unchanged native world settings. */
    public static Object addLanPauseRow(Object rows, Object nativeExit, int columns, Object pauseScreen) {
        ClassLoader loader = pauseScreen.getClass().getClassLoader();
        try {
            Class<?> elementType = loadClass(loader, aliases("layoutElementClasses"));
            Object minecraft = null;
            Object server = null;
            Boolean published = null;
            try {
                Class<?> minecraftType = loadClass(loader, aliases("minecraftClasses"));
                minecraft = PortableIdentityReflection.invokeStatic(
                    minecraftType, new Class<?>[0], new Object[0], aliases("minecraftGetInstanceMethods"));
                if (minecraft != null) {
                    server = PortableIdentityReflection.invoke(minecraft, aliases("getSingleplayerServerMethods"));
                }
                if (server != null) {
                    Object state = PortableIdentityReflection.invoke(server, aliases("isPublishedMethods"));
                    if (state instanceof Boolean value) published = value;
                }
            } catch (ReflectiveOperationException unknownState) {
                // No integrated server or an unknown state keeps the original
                // menu. It must not offer a LAN action on a remote connection.
            }
            if (server != null && published != null) {
                Object button = explicitLanPauseButton(pauseScreen, minecraft, published, loader);
                Object width = PortableIdentityReflection.invoke(nativeExit, aliases("widgetGetWidthMethods"));
                PortableIdentityReflection.invokeDeclared(button,
                    new Class<?>[] { int.class }, new Object[] { width }, aliases("widgetSetWidthMethods"));
                PortableIdentityReflection.invokeDeclared(rows,
                    new Class<?>[] { elementType, int.class }, new Object[] { button, columns },
                    aliases("pauseRowAddMethods"));
            }
            return PortableIdentityReflection.invokeDeclared(rows,
                new Class<?>[] { elementType, int.class }, new Object[] { nativeExit, columns },
                aliases("pauseRowAddMethods"));
        } catch (ReflectiveOperationException exception) {
            throw new IllegalStateException("The native LAN/reporting pause row could not be built.", exception);
        }
    }

    private static Object explicitLanPauseButton(
        Object pauseScreen, Object minecraft, boolean published, ClassLoader loader)
        throws ReflectiveOperationException {
        Class<?> componentType = loadClass(loader, aliases("componentClasses"));
        Class<?> screenType = loadClass(loader, aliases("screenClasses"));
        Object label;
        java.util.function.Supplier<?> screen;
        if (published) {
            label = staticField(pauseScreen.getClass(), aliases("pausePlayerReportingFields"));
            var constructor = loadClass(loader, aliases("reportingScreenClasses")).getDeclaredConstructor(screenType);
            constructor.setAccessible(true);
            screen = () -> constructScreen(constructor, pauseScreen);
        } else {
            // This is Minecraft's own translated Open to LAN caption. Its
            // world-options caption and action remain in the original row.
            label = PortableIdentityReflection.invokeStatic(componentType,
                new Class<?>[] { String.class }, new Object[] { "menu.multiplayerOptions.button" },
                aliases("componentTranslatableMethods"));
            Class<?> levelType = loadClass(loader, aliases("levelClasses"));
            Object level = PortableIdentityReflection.getField(minecraft, aliases("minecraftLevelFields"));
            var constructor = loadClass(loader, aliases("worldOptionsScreenClasses"))
                .getDeclaredConstructor(screenType, levelType);
            constructor.setAccessible(true);
            screen = () -> {
                Object requested = constructScreen(constructor, pauseScreen, level);
                REQUESTED.put(requested, Boolean.TRUE);
                return requested;
            };
        }
        return PortableIdentityReflection.invokeDeclared(pauseScreen,
            new Class<?>[] { componentType, java.util.function.Supplier.class }, new Object[] { label, screen },
            aliases("pauseOpenScreenButtonMethods"));
    }

    private static Object constructScreen(java.lang.reflect.Constructor<?> constructor, Object... arguments) {
        try {
            return constructor.newInstance(arguments);
        } catch (ReflectiveOperationException exception) {
            throw new IllegalStateException("The native pause action screen could not be opened.", exception);
        }
    }

    /** The native multiplayer pause slot, with reporting after a successful publication. */
    public static Object pauseButton(
        Object pauseScreen, Object nativeLabel, java.util.function.Supplier<?> nativeScreen) {
        ClassLoader loader = pauseScreen.getClass().getClassLoader();
        try {
            Class<?> componentType = loadClass(loader, aliases("componentClasses"));
            Object label = nativeLabel;
            java.util.function.Supplier<?> screen = nativeScreen;
            if (publishedIntegratedWorld(loader)) {
                Class<?> screenType = loadClass(loader, aliases("screenClasses"));
                Class<?> reportingType = loadClass(loader, aliases("reportingScreenClasses"));
                var constructor = reportingType.getDeclaredConstructor(screenType);
                constructor.setAccessible(true);
                label = staticField(pauseScreen.getClass(), aliases("pausePlayerReportingFields"));
                screen = () -> {
                    try {
                        return constructor.newInstance(pauseScreen);
                    } catch (ReflectiveOperationException exception) {
                        throw new IllegalStateException("The native reporting screen could not be opened.", exception);
                    }
                };
            }
            return PortableIdentityReflection.invokeDeclared(
                pauseScreen,
                new Class<?>[] { componentType, java.util.function.Supplier.class },
                new Object[] { label, screen },
                aliases("pauseOpenScreenButtonMethods"));
        } catch (ReflectiveOperationException exception) {
            throw new IllegalStateException("The native multiplayer pause button could not be built.", exception);
        }
    }

    private static boolean publishedIntegratedWorld(ClassLoader loader) {
        try {
            Class<?> minecraftType = loadClass(loader, aliases("minecraftClasses"));
            Object minecraft = PortableIdentityReflection.invokeStatic(
                minecraftType, new Class<?>[0], new Object[0], aliases("minecraftGetInstanceMethods"));
            Object server = PortableIdentityReflection.invoke(minecraft, aliases("getSingleplayerServerMethods"));
            return server != null && Boolean.TRUE.equals(PortableIdentityReflection.invoke(
                server, aliases("isPublishedMethods")));
        } catch (ReflectiveOperationException exception) {
            // An unknown state must keep the native multiplayer slot, rather
            // than pretend a failed or missing publication succeeded.
            return false;
        }
    }

    public static boolean autoPublish(Object screen) {
        // WorldOptionsScreen also owns difficulty, gamerules and permissions.
        // Its normal entry must never publish or dismiss it. Only the screen
        // deliberately constructed by our explicit LAN action is eligible.
        if ("true".equals(System.getProperty("minecraft.portable.identity.lanPublishExplicitRequestOnly")) &&
            REQUESTED.remove(screen) == null) {
            return false;
        }
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
                // Already open. The settings screen has nothing left to offer -
                // the port is taken and the access mode belongs to the mod that
                // carries the session - so the press does what the first one
                // did: nothing visible. A menu that keeps its "open to LAN"
                // button after the world is open would otherwise walk a player
                // into a screen that cannot change anything.
                closeScreenSoon(minecraftType, screenType);
                return true;
            }

            int port = (Integer) PortableIdentityReflection.invokeStatic(
                httpUtilType,
                new Class<?>[0],
                new Object[0],
                aliases("getAvailablePortMethods", "getAvailablePort", "a"));

            // Publish before closing the screen so every failure path falls
            // through to the untouched vanilla settings UI.
            boolean published;
            if (hasAll("multiplayerScopeClasses", "lanMultiplayerScopeFields")) {
                // 26.2's native screen publishes a scope and a port. Its
                // two-argument overload keeps the existing per-guest mode and
                // commands setting; the four-argument convenience overload
                // would write those settings again.
                Class<?> scopeType = loadClass(loader, aliases("multiplayerScopeClasses"));
                Object lanScope = staticField(scopeType, aliases("lanMultiplayerScopeFields"));
                published = Boolean.TRUE.equals(PortableIdentityReflection.invokeDeclared(
                    server,
                    new Class<?>[] { scopeType, int.class },
                    new Object[] { lanScope, port },
                    aliases("publishServerMethods", "publishServer")));
            } else {
                // The values the older vanilla screen would pre-select:
                // the world's own mode and command permission.
                Object gameType = PortableIdentityReflection.invoke(
                    server,
                    aliases("getDefaultGameTypeMethods", "getDefaultGameType", "u_"));
                Object worldData = PortableIdentityReflection.invoke(
                    server,
                    aliases("getWorldDataMethods", "getWorldData", "bb"));
                boolean commands = Boolean.TRUE.equals(PortableIdentityReflection.invoke(
                    worldData,
                    aliases("isAllowCommandsMethods", "isAllowCommands", "m")));
                published = Boolean.TRUE.equals(PortableIdentityReflection.invokeDeclared(
                    server,
                    new Class<?>[] { gameTypeType, boolean.class, int.class },
                    new Object[] { gameType, commands, port },
                    aliases("publishServerMethods", "publishServer", "a")));
            }
            if (!published) {
                System.out.println(
                    "[PortableIdentity] LAN auto-publish could not bind port " + port +
                    "; opening the vanilla screen.");
                return false;
            }

            try {
                closeScreenSoon(minecraftType, screenType);
                sayItInChat(minecraft, publishCommandType, componentType, port);
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

    /**
     * The line vanilla writes in chat when a world is published, best effort.
     *
     * <p>Not every version has it under a name this can find: on 1.18.2 the
     * lookup for getSuccessMessage throws NoSuchMethodException every single
     * launch, and it used to take the title update down with it and print an
     * exception into the log of a session where nothing had gone wrong. A
     * version that cannot say the world is published has still published it,
     * so a missing method here is silence rather than a failure.</p>
     *
     * <p>Anything else that goes wrong is still thrown, because that is a real
     * fault and the caller writes it down.</p>
     */
    private static void sayItInChat(
        Object minecraft,
        Class<?> publishCommandType,
        Class<?> componentType,
        int port) throws ReflectiveOperationException {
        Object message;
        try {
            message = PortableIdentityReflection.invokeStatic(
                publishCommandType,
                new Class<?>[] { int.class },
                new Object[] { port },
                aliases("publishSuccessMethods", "getSuccessMessage", "a"));
        } catch (NoSuchMethodException absentOnThisVersion) {
            return;
        }

        Object gui = PortableIdentityReflection.getField(
            minecraft,
            aliases("minecraftGuiFields", "gui", "l"));
        if (hasAll("guiHudFields")) {
            gui = PortableIdentityReflection.getField(gui, aliases("guiHudFields"));
        }
        Object chat = PortableIdentityReflection.invoke(
            gui,
            aliases("guiChatMethods", "getChat", "d"));
        PortableIdentityReflection.invokeDeclared(
            chat,
            new Class<?>[] { componentType },
            new Object[] { message },
            aliases("chatAddMessageMethods", "addMessage", "a"));
    }

    /** Whether the launcher found every name this needs in the game it started. */
    private static boolean hasAll(String... propertyNames) {
        for (String name : propertyNames) {
            String value = System.getProperty("minecraft.portable.identity." + name);
            if (value == null || value.isBlank()) {
                return false;
            }
        }
        return true;
    }

    /**
     * Puts the screen away, which is what the vanilla one does when it closes -
     * but not this instant. The screen is in the middle of being built, and
     * mods look at what it built as soon as it is done; closing it out from
     * under them is how the game went down. When the native scheduling method
     * is known, queue explicitly: Executor.execute may run inline on the render
     * thread. Closing in the next client task lets native/mod init hooks finish.
     */
    private static void closeScreenSoon(Class<?> minecraftType, Class<?> screenType)
        throws ReflectiveOperationException {
        Object minecraft = PortableIdentityReflection.invokeStatic(
            minecraftType,
            new Class<?>[0],
            new Object[0],
            aliases("minecraftGetInstanceMethods", "getInstance", "Q"));
        Runnable close = () -> {
            try {
                Object receiver = "true".equals(System.getProperty(
                    "minecraft.portable.identity.setScreenOnGui"))
                    ? PortableIdentityReflection.getField(
                        minecraft, aliases("minecraftGuiFields", "gui"))
                    : minecraft;
                PortableIdentityReflection.invokeDeclared(
                    receiver,
                    new Class<?>[] { screenType },
                    new Object[] { null },
                    aliases("setScreenMethods", "setScreen", "a"));
            } catch (Throwable exception) {
                System.out.println(
                    "[PortableIdentity] LAN share screen could not be closed: " + exception);
            }
        };
        if (hasAll("minecraftScheduleMethods")) {
            PortableIdentityReflection.invokeDeclared(
                minecraft,
                new Class<?>[] { Runnable.class },
                new Object[] { close },
                aliases("minecraftScheduleMethods"));
        } else if (minecraft instanceof java.util.concurrent.Executor client) {
            client.execute(close);
        } else {
            close.run();
        }
    }

    private static Object staticField(Class<?> type, String... names)
        throws ReflectiveOperationException {
        for (String name : names) {
            for (Class<?> current = type; current != null; current = current.getSuperclass()) {
                try {
                    var field = current.getDeclaredField(name);
                    field.setAccessible(true);
                    return field.get(null);
                } catch (NoSuchFieldException ignored) {
                    // Try the declaring superclass, then the runtime alias.
                }
            }
        }
        throw new NoSuchFieldException(type.getName() + "." + String.join("/", names));
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
