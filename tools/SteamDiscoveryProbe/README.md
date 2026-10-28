# Steam worlds in the multiplayer list

Run with JDK 17 or newer:

```powershell
$env:JAVA_HOME = 'C:\Tools\jdk-25.0.2'
.\tools\SteamDiscoveryProbe\Run-Probe.ps1
```

The probe compiles the production discovery transformer and bootstrap hooks,
transforms a screen fixture, and loads its result with JVM `-Xverify:all`.
Deterministic native-service fakes exercise Steam worker confinement, compatible
friend and owner filtering, transient LAN entries, the author's selected-lobby
join path, ordinary server joins, screen resize and closure, invalid/stale
addresses, submission failure and recovery after a transient worker error.
It never initializes real Steam, sends invitations or connects two accounts.
Output is kept under `artifacts/atm9-original-steam/discovery-probe`.

The actual integration targets the reviewed e4steam 0.3.2 and 0.3.4 interfaces in
`SteamTransportCatalog`. `SteamRuntime.submitSteamTask(Callable)` owns every
native operation; `SteamLobbyManager.requestJoin(SteamID, SteamID, boolean)`
retains the author's friendship, owner, protocol and private-endpoint checks.
The list carries only lobby identifiers, never private endpoints or tokens,
and does not write `servers.dat`. Minecraft members are derived from the
prepared runtime mappings and checked against real JARs by the adapter
preflight. Real Steam overlay and two-account joins require separate runtime
evidence.

`Test-SteamHealth.ps1` checks the Windows client-process probe from both pinned
SDK DLLs without initializing Steam, running callbacks or reading account IDs:

```powershell
.\tools\SteamDiscoveryProbe\Test-SteamHealth.ps1 -RequireHealthy
```

Use `-InstallRoot` for another launcher installation and `-GameJavaHome` for a
QA game's `-Duser.home`. The default home is `Minecraft/Personal/Home` inside
the selected installation. JSON contains only availability and boolean health
and registry-presence checks. A missing or hash-mismatched DLL is never loaded.
`-RequireHealthy` returns exit code 1 unless both native probes return true.
This is a pre-launch environment check; it does not establish Steam logon,
overlay, lobby stability or a successful peer connection. It does not change
the Steam registry or processes and does not disable e4steam's native guard.

`Test-SteamReadiness.ps1 -RequireReady` performs a separate, short SDK session
using the pinned native `SteamAPI_InitFlat` and `ISteamUser.BLoggedOn` exports.
It shuts down only that diagnostic session and reports booleans. Native stdout
and stderr stay in an isolated child pipe because SDK startup can print an
account identifier. It sends no messages and changes no registry or Steam
process. `ready` requires successful initialization and logon; the optional
client-process hint is reported separately, consistent with the author's
reviewed 0.3.4 behavior. This still does not establish a peer connection.

## Forge 0.3.4 transport review

Only the Forge Minecraft `[1.18.2,1.20.3)` row changes from 0.3.2 to the author's
[0.3.4 release](https://github.com/Kamilhik/e4steam/releases/tag/v0.3.4).
The new JAR is 3,817,117 bytes, SHA256
`33421acbea4d904ac3cefa62ac1d4bd5f62f9e9169b81a4d16a052d2330bc6fc`;
the retired 0.3.2 bytes are recognized by their exact SHA256
`9a0fc4e1a8c170f37e04cf8469091990d5c1f0953b334481dbb5eab9475e3e1a`.
Other existing transports keep their pins, including ATM10's NeoForge 0.3.2.

The author's `SteamClientHealthMonitor.nativeProbeConfirmed` change fixes a
15-second teardown when a native client-process hint has never confirmed an
otherwise initialized Steam API. A confirmed client that later disappears
still fails the three-probe guard. No local guard is disabled. Replay the
author's regression against each actual, hash-verified compiled release class:

```powershell
.\tools\SteamDiscoveryProbe\Test-SteamTransportHealth.ps1 -TransportJar <reviewed-Forge-JAR>
```

The actual 0.3.4 JAR retains `SteamRuntime.submitSteamTask(Callable)`, its
`lobbyManager`, `SteamLobbyManager.friends`/`matchmaking` and
`requestJoin(SteamID,SteamID,boolean)`. Rich presence uses `e4steam-lobby:`,
metadata uses `e4steam_protocol` and `e4steam_minecraft`, protocol remains 4,
and the author's owner, friendship and endpoint checks remain. The discovery
version property comes from the selected, reviewed platform transport.

Forge 0.3.4 redirects `ShareToLanScreen.init` to its own form through a
cancellable HEAD Mixin. The launcher therefore publishes after every normal
`init` return, including the author's early return, while retaining the native
body and the per-screen attempt guard. `IdentityAdapterLanReturnTests` executes
the real Forge hook against both return paths and checks initialized widgets,
world defaults, repeated entry, resize and the failed-bind fallback. Its scope
is JVM lifecycle behavior; the final clean launcher and author JAR still need
their separate real client/world/one-click check.

Safety review: this is a managed network transport installed by the launcher,
outside the author pack payload. It adds no world-changing ability, content
mod, terrain/inventory path or custom Safety helper. Existing author JARs,
configs, scripts and resources remain unchanged, so no Safety profile or mod
change is required. Steam still authenticates and authorizes peer ingress.
