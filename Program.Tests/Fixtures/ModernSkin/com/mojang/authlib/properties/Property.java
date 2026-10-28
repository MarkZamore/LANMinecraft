package com.mojang.authlib.properties;

/** Texture payload container for the isolated transformer contract test. */
public final class Property {
    public final String name;
    public final String value;

    public Property(String name, String value) {
        this.name = name;
        this.value = value;
    }
}
