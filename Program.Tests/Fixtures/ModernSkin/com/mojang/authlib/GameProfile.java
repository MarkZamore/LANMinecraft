package com.mojang.authlib;

import java.util.HashMap;
import java.util.UUID;

/** Minimal mutable profile for the native lookup transformer contract test. */
public final class GameProfile {
    private final UUID id;
    private final String name;
    private final Properties properties = new Properties();

    public GameProfile(UUID id, String name) {
        this.id = id;
        this.name = name;
    }

    public Properties properties() {
        return properties;
    }

    public static final class Properties extends HashMap<String, Object> {
        public void removeAll(String key) {
            remove(key);
        }
    }
}
