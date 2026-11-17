package com.mojang.serialization;

/** Minimal test double with DFU's singleton stable/experimental contract. */
public class Lifecycle {
    private static final Lifecycle STABLE = new Lifecycle();
    private static final Lifecycle EXPERIMENTAL = new Lifecycle();
    public static Lifecycle stable() { return STABLE; }
    public static Lifecycle experimental() { return EXPERIMENTAL; }
    public static Lifecycle deprecated(int since) { return new Lifecycle(); }
    public String toString() { return this == EXPERIMENTAL ? "Experimental" : "Other"; }
}
