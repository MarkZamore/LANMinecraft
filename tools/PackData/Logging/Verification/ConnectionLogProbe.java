import org.apache.logging.log4j.LogManager;
import org.apache.logging.log4j.Logger;
import org.apache.logging.log4j.MarkerManager;
public class ConnectionLogProbe {
 public static void main(String[] args) {
  Logger log = LogManager.getLogger("net.minecraft.network.Connection");
  for (String text : new String[]{"Failed to sent packet", "Double fault", "Skipping packet due to errors", "Timeout"})
   log.debug(text, new ArrayIndexOutOfBoundsException("synthetic diagnostic " + text));
  log.debug(MarkerManager.getMarker("NETWORK_PACKETS"), "UNWANTED_PACKET_TRACE synthetic");
  log.debug("UNWANTED_OTHER_DEBUG", new IllegalStateException("synthetic unrelated"));
  log.info("EXPECTED_INFO synthetic");
  log.warn("EXPECTED_WARN synthetic");
  log.error("EXPECTED_ERROR synthetic", new IllegalStateException("synthetic retained error"));
  LogManager.shutdown();
 }
}