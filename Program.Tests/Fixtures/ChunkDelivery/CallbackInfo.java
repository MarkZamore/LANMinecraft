package org.spongepowered.asm.mixin.injection.callback;

/** Minimal cancellation contract used by the chunk delivery regression fixture. */
public final class CallbackInfo {
    private boolean cancelled;

    public void cancel() { cancelled = true; }
    public boolean isCancelled() { return cancelled; }
}
