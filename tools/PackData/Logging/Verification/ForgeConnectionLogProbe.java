package net.minecraft.network;

import com.mojang.logging.LogUtils;
import org.apache.logging.log4j.LogManager;
import org.slf4j.Logger;
import org.slf4j.MarkerFactory;

final class Connection {
    private static final Logger LOGGER = LogUtils.getLogger();

    public static void main(String[] args) {
        if (!Connection.class.getName().equals(LOGGER.getName())) {
            throw new AssertionError("Mojang selected another logger: " + LOGGER.getName());
        }
        for (String label : new String[] {"Skipping packet due to errors", "Timeout", "Failed to sent packet", "Double fault"}) {
            LOGGER.debug(label, new IllegalStateException("bounded diagnostic", new IllegalArgumentException("nested cause")));
        }
        LOGGER.debug(MarkerFactory.getMarker("NETWORK_PACKETS"), "UNWANTED_PACKET_TRACE");
        LOGGER.debug("UNWANTED_OTHER_DEBUG", new IllegalStateException("unrelated diagnostic"));
        LOGGER.trace("UNWANTED_TRACE");
        LOGGER.info("EXPECTED_INFO");
        LOGGER.warn("EXPECTED_WARN");
        LOGGER.error("EXPECTED_ERROR", new IllegalStateException("ordinary error"));
        LogManager.shutdown();
    }
}
