# Name-keyed player profiles

Minecraft 1.6.4 identifies native player data by name. The launcher records this
platform explicitly as `minecraftVersion: "1.6.4"` and
`playerStorage: "legacy-name-v1"` in world metadata. A missing UUID is never
evidence of a legacy world: modern corruption continues to fail validation.
The selected runtime may establish the format for worlds physically inside its
build directory; a conflicting recorded format stops preparation.

`LegacyPlayerIdentity.NativeName` encodes the full individual SteamID64 as `s`
followed by thirteen zero-padded lower-case base36 characters. It is injective,
fourteen ASCII characters long and independent of the player's chosen display
name. Names have no authentication power. The e4steam legacy runtime must bind
them to its authenticated live bridge or native local host before login.

The ordinary name/UUID identity registry retains its existing format. A separate
UTF-8 `legacy-identity-profiles.properties` contains exactly four pipe-separated
fields: SteamID64, native name, Base64 UTF-8 display name, portable UUID. Base64
preserves previously saved Unicode names and delimiters without allowing extra
registry fields or lines; ordinary nickname validation is unchanged. The JVM property is
`minecraft.portable.legacy.registry`. The registry is presentation data and is
updated through the same observed peers as the ordinary registry. The skin
registry is unchanged. Native storage and account UUIDs are derived from Steam,
not from a peer's self-reported UUID.

## Preparation and transfer

Launch preparation is scoped to the selected build's world directory. It does
not validate or rewrite the worlds of unrelated builds. Legacy preparation reads
and writes `players/<nativeName>.dat`, retaining the original player compound
without injecting UUID tags or creating a modern `playerdata` directory. Legacy
worlds legitimately lack the modern `WorldGenSettings` compound.

Before replacing `Data.Player`, the current host compound is exported using its
recorded owner. `legacyHostSteamId64` is established only by an actually played
session (`session.lock` changed during the session) or after importing a known
player's profile. Runtime preparation and world-list refresh do not assign this
field to a different player. A prepared manifest's last holder is a fallback
when the game saved a new character before the session stamp was available.

The process service captures the launch identity, exact platform, build path and
instance directory before starting the child JVM. Its exit monitor stamps worlds
with that immutable capture, even if the UI has since rebound another account or
selected another build. The UI never reconstructs the finished game's identity.
Adopted processes have no proven launch capture and skip host attribution.

An imported host with no account mapping is preserved as a hash-named unassigned
file. Existing human-named legacy profiles remain unassigned as well. Neither
is claimed by whichever account happens to launch the world. Transfer uses the
existing staging copy and transaction journal: it exports the host, removes
`Data.Player` in the snapshot, then the receiver imports only its own profile.
Forwarding a received world without playing first preserves both characters.

The legacy manifest validates native-name/SteamID/portable-UUID bindings and
hashes every preserved top-level file in `players` and `stats`, including an
explicit unassigned list. It rejects path traversal, changed bytes, duplicates
and assigning another account's file to a player. Generic files elsewhere in the
world remain part of the normal world snapshot and are not rewritten.

The shared `PortableFormat` schema and protocol are 12. Older peers reject the
new transfer instead of silently applying UUID-based preparation.

## Client statistics stay local

The actual Minecraft 1.6.4 client writes personal statistics in
`GAME/stats/stats_<nativeName>_unsent.dat` and `.old`, outside the world. The
instance service treats the root `stats` directory as personal even if a source
pack, managed file list, ownership rule or older instance state claims it.
Repeated preparation, content updates and ordinary cleanup preserve these files.
Stable native names keep their local keys unchanged when a display nickname
changes. Existing unknown/native nickname files are also retained.

These per-instance client statistics are not added to world transfers or the
published pack. There is no new cross-machine client-statistics synchronization.
The legacy world manifest's `world/stats` retention is a conservative file
preservation rule, not a claim that 1.6.4 writes its native statistics there.

## Verification boundary

`LegacyWorldPlayerTests`, `LegacyIdentityRegistryTests`, `WorldSteamSchemaTests`,
`WorldGenSettingsGuardTests`, `PortableFormatTests` and
`PortableIdentityRegistryTests` cover the isolated storage/registry behavior.
`WorldLaunchSessionTests` covers account/build changes between capture and exit,
including preservation of both accounts' inventories and unknown adopted runs.
The native-statistics instance regression embeds the exact synthetic QA file
documented in `Program.Tests/Fixtures/LegacyStats/README.md` and checks byte
retention through source claims, updates, preparation and cleanup.
The host-swap fixture checks nested character data and retention of an untouched
name-owned payload. It does not prove an actual pet or mod progression runtime.
Actual Minecraft, BTW/Nightmare-owned entities, global client statistics, selected
skins and authenticated two-account Steam play require separate runtime checks.
