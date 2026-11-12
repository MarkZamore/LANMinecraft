# Bounded Connection exception diagnostics

The six published profiles use Minecraft 1.20.1 / Forge 47.3.7 or 47.4.0, Minecraft 1.21.1 / NeoForge 21.1.248 or 21.1.265, and Minecraft 26.3 / NeoForge 26.3.0.99. Their exact native JAR and Connection class hashes are recorded in `all-current-callers-proof.json`.

Each native `net.minecraft.network.Connection.exceptionCaught` emits four caught Throwable diagnostics at DEBUG: `Skipping packet due to errors`, `Timeout`, `Failed to sent packet` (the native spelling), and `Double fault`. The static initializer obtains the class logger through `LogUtils.getLogger`. Modern callers additionally emit `Exception caught in connection` at ERROR; normal INFO/WARN/ERROR are retained by the threshold filter.

The narrow logger/filter retains only these four DEBUG diagnostics in the existing rolling latest.log and rejects unrelated Connection DEBUG output, including packet traces. It does not create debug.log, change packet traffic, fix a disconnect, alter mechanics or prove multiplayer success. Healthy connections add no matching exception diagnostics.

Reproduce the isolated filter integration with real Log4j API/core JARs for each recorded version (2.19.0, 2.22.1 and 2.26.0): compile `ConnectionLogProbe.java` with those two JARs on the classpath, then run it in an empty temporary directory with `-Dlog4j.configurationFile=<absolute path to connection-throwable-filter.probe.xml>`. The generated `probe-filter.log` must contain four ArrayIndexOutOfBoundsException stacks, EXPECTED_INFO, EXPECTED_WARN and EXPECTED_ERROR with its stack. It must not contain UNWANTED_PACKET_TRACE or UNWANTED_OTHER_DEBUG. The fixture uses a standard pattern; unchanged Minecraft custom formatters are outside this isolated test.

These are bytecode and real logging-library checks. On future Minecraft or loader changes, recheck the native caller, logger and Throwable-bearing messages against the new JAR.

## Forge 1.18.2 and 1.19.2 with Log4j 2.17.0

`forge-log4j217-proof.json` pins the exact client JARs and Connection classes.
Both call SLF4J `debug(String, Throwable)` with the same four labels and use
`LogUtils.getLogger()`. Their inherited Mojang XML uses `LegacyXMLLayout` in
the console. The Forge template keeps that layout and adds explicit full
throwables and bounded rollovers to the file appender.

To reproduce, run `ForgeConnectionLogProbe.java` with Java 17 source-file
launch in an empty owned working directory. Pass the pinned API, core,
SLF4J binding, SLF4J API and Mojang logging JARs on the classpath and use
`-Dlog4j.configurationFile=<absolute path to log4j2-forge-1.18-1.19.xml>`.
The fixture checks the actual logger name. The output must have three native
console records, and `logs/latest.log` must retain four nested cause chains
plus INFO, WARN and ERROR while rejecting the packet and other DEBUG or TRACE
messages. Only `latest.log` is expected from the fixture.

## Forge 1.12.2 and 1.16.5 with Java 8

`forge-java8-proof.json` pins the exact inputs. For 1.12.2 the probe inspected
the `gw` client class after applying the corresponding Forge binary patch.
Both class versions use `LogManager.getLogger()` and log a localized reason
with its Throwable. The three other DEBUG sites emit `Enabled auto read`,
`Disabled auto read` and `Set listener of {} to {}`. The 1.12 template
filters these messages and the packet marker while allowing localized causes.
The 1.16.5 patched client uses the four fixed labels on the NetworkManager
logger. Both templates retain the inherited Mojang Patchy console layout.

Compile `LegacyForgeConnectionLogProbe.java` with `--release 8 -encoding UTF-8`
and a pinned Log4j API JAR. Run its `net.minecraft.network.NetworkManager`
class on the pinned Java 8 runtime with Log4j API, core and Mojang Patchy
on the classpath, from an empty owned working directory. Pass the exact XML
with `-Dlog4j.configurationFile`, then run `localized` with both 2.8.1 and
2.15.0 using the 1.12 template and `fixed` with 2.15.0 using the 1.16 template.
`logs/latest.log` must contain respectively two or four nested cause chains,
normal INFO, WARN and ERROR, and no packet or unrelated native DEBUG messages.
