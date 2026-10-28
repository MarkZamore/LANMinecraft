package diagnostics;

import java.lang.reflect.*;
import java.util.function.BooleanSupplier;

/** Replays the author's health regression against the actual release class. */
public final class SteamTransportHealthProof {
    private static void require(boolean value, String detail) {
        if (!value) throw new AssertionError(detail);
    }
    public static void main(String[] args) throws Exception {
        boolean fixed = Boolean.parseBoolean(args[0]);
        Class<?> type = Class.forName("link.e4steam.steam.SteamClientHealthMonitor");
        Constructor<?> ctor = type.getDeclaredConstructor(boolean.class, long.class, int.class);
        ctor.setAccessible(true);
        Method healthy = type.getDeclaredMethod("isHealthy", long.class, BooleanSupplier.class);
        Method reset = type.getDeclaredMethod("reset");
        healthy.setAccessible(true); reset.setAccessible(true);
        Object monitor = ctor.newInstance(true, 5000L, 3);
        BooleanSupplier absent = () -> false, present = () -> true;
        if (!fixed) {
            require((Boolean) healthy.invoke(monitor, 0L, absent), "baseline scheduling");
            require((Boolean) healthy.invoke(monitor, 5000L, absent), "baseline first failure");
            require((Boolean) healthy.invoke(monitor, 10000L, absent), "baseline second failure");
            require(!(Boolean) healthy.invoke(monitor, 15000L, absent), "baseline reproduces false teardown at 15 seconds");
            System.out.println("PASS: actual baseline release class reproduces never-confirmed probe teardown at 15 seconds.");
            return;
        }
        for (long now = 0; now <= 120000; now += 5000)
            require((Boolean) healthy.invoke(monitor, now, absent), "unconfirmed process hint must not terminate initialized API");
        require((Boolean) healthy.invoke(monitor, 125000L, present), "native confirmation");
        require((Boolean) healthy.invoke(monitor, 130000L, absent), "confirmed first failure");
        require((Boolean) healthy.invoke(monitor, 135000L, absent), "confirmed second failure");
        require(!(Boolean) healthy.invoke(monitor, 140000L, absent), "confirmed loss still triggers guard after three failures");
        reset.invoke(monitor);
        for (long now = 145000; now <= 180000; now += 5000)
            require((Boolean) healthy.invoke(monitor, now, absent), "reset requires fresh confirmation");
        System.out.println("PASS: actual author release class preserves 120 seconds of unconfirmed hints, retains confirmed-loss guard and resets confirmation. No native Steam/API initialized.");
    }
}
