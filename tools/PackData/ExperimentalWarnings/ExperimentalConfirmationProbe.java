package net.minecraft;

import com.mojang.serialization.Lifecycle;

/** Executes the same branch shapes used by Minecraft 1.16.5 through 26.3. */
public final class ExperimentalConfirmationProbe {
    private static int prompts;
    private static int proceeded;
    private static int cancelled;

    private static void create(Lifecycle lifecycle, Runnable proceed) {
        if (lifecycle == Lifecycle.stable()) {
            proceed.run();
        } else if (lifecycle == Lifecycle.experimental()) {
            prompt("selectWorld.warning.experimental.title");
        } else {
            prompt("selectWorld.warning.deprecated.title");
        }
    }

    private void importSettings(Lifecycle lifecycle) {
        Runnable consumer = new Runnable() { public void run() { proceeded++; } };
        if (lifecycle == Lifecycle.stable()) {
            consumer.run();
        } else if (lifecycle == Lifecycle.experimental()) {
            prompt("selectWorld.import_worldgen_settings.experimental.title");
        } else {
            prompt("selectWorld.import_worldgen_settings.deprecated.title");
        }
    }

    private void load(boolean customized, Runnable proceed, Runnable cancel) {
        prompt(customized ? "selectWorld.backupQuestion.customized" : "selectWorld.backupQuestion.experimental");
    }

    private void legacyLoad(Object dialog, String world, boolean customized, Runnable proceed) {
        prompt(customized ? "selectWorld.backupQuestion.customized" : "selectWorld.backupQuestion.experimental");
    }

    private static void prompt(String title) { prompts++; }
    private static void require(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }

    public static void main(String[] arguments) {
        ExperimentalConfirmationProbe probe = new ExperimentalConfirmationProbe();
        Runnable proceed = new Runnable() { public void run() { proceeded++; } };
        Runnable cancel = new Runnable() { public void run() { cancelled++; } };
        create(Lifecycle.stable(), proceed);
        create(Lifecycle.experimental(), proceed);
        require(proceeded == 2 && prompts == 0, "creation stable/experimental");
        create(Lifecycle.deprecated(1), proceed);
        require(proceeded == 2 && prompts == 1, "deprecated creation");
        probe.importSettings(Lifecycle.stable());
        probe.importSettings(Lifecycle.experimental());
        require(proceeded == 4 && prompts == 1, "import stable/experimental");
        probe.importSettings(Lifecycle.deprecated(1));
        require(proceeded == 4 && prompts == 2, "deprecated import");
        probe.load(false, proceed, cancel);
        probe.legacyLoad(null, "fixture", false, proceed);
        require(proceeded == 6 && prompts == 2 && cancelled == 0, "experimental opening");
        probe.load(true, proceed, cancel);
        probe.legacyLoad(null, "fixture", true, proceed);
        require(proceeded == 6 && prompts == 4 && cancelled == 0, "customized opening");
        require(Lifecycle.experimental().toString().equals("Experimental"), "experimental metadata retained");
        System.out.println("PASS creation/import/opening, deprecated/customized gates, metadata, callbacks");
    }
}
