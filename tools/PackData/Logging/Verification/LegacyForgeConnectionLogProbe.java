package net.minecraft.network;

import org.apache.logging.log4j.LogManager;
import org.apache.logging.log4j.Logger;
import org.apache.logging.log4j.MarkerManager;

final class NetworkManager {
    private static final Logger LOGGER = LogManager.getLogger();

    public static void main(String[] args) {
        if (!NetworkManager.class.getName().equals(LOGGER.getName())) {
            throw new AssertionError("Log4j selected another logger: " + LOGGER.getName());
        }
        boolean localized = args[0].equals("localized");
        String[] labels = localized ? new String[] {"Read timed out", "Connexion interrompue"}
            : new String[] {"Skipping packet due to errors", "Timeout", "Failed to sent packet", "Double fault"};
        for (String label : labels) {
            LOGGER.debug(label, new IllegalStateException("bounded diagnostic", new IllegalArgumentException("nested cause")));
        }
        LOGGER.debug(MarkerManager.getMarker("NETWORK_PACKETS"), "UNWANTED_PACKET_TRACE");
        LOGGER.debug("Enabled auto read");
        LOGGER.debug("Disabled auto read");
        LOGGER.debug("Set listener of {} to {}", "channel", "listener");
        if (!localized) LOGGER.debug("UNWANTED_OTHER_DEBUG", new IllegalStateException("unrelated diagnostic"));
        LOGGER.trace("UNWANTED_TRACE");
        LOGGER.info("EXPECTED_INFO");
        LOGGER.warn("EXPECTED_WARN");
        LOGGER.error("EXPECTED_ERROR", new IllegalStateException("ordinary error"));
        LogManager.shutdown();
    }
}
