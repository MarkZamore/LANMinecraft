# e4steam native-init coexistence fixture

`upstream-fixtures.zip` contains unmodified class entries extracted from these verified author artifacts:

- [e4steam v0.3.4](https://github.com/Kamilhik/e4steam/releases/tag/v0.3.4), NeoForge `e4steam-neoforge-mc1.20.2-26.3-v0.3.4.jar`, SHA-256 `b7c388393b7c27d188c17981073a2471c7ac52b95d5cff8b8dc8f19946d67be0`: the ShareToLan, MultiplayerOptions and WorldOptions mixin classes.
- The same author release, Forge `e4steam-forge-mc1.18.2-1.20.2-v0.3.4.jar`, SHA-256 `33421acbea4d904ac3cefa62ac1d4bd5f62f9e9169b81a4d16a052d2330bc6fc`: the ShareToLan mixin class under `forge/`, preserving its SRG method aliases.
- [Fabric Sponge Mixin 0.17.4+mixin.0.8.7](https://maven.fabricmc.net/net/fabricmc/sponge-mixin/0.17.4+mixin.0.8.7/), JAR SHA-256 `1f0ae44db7295f8626f33b1dc0ad7f043d8954a8d6847247875fcc5dfcecc934`: actual CallbackInfo, CallbackInfoReturnable, Cancellable and CancellationException classes.

Per-entry lengths, SHA-256 and source JAR hashes are recorded in `upstream-entries.json`. The ZIP includes e4steam's Apache-2.0 license (`META-INF/LICENSE-e4steam.txt`) and Mixin's MIT license (`LICENSE.txt`). These are test inputs; the subset is not an installable game mod or a shipped transport.

`LanRedirectCoexistenceProbe.java` reads the real author handler bodies and preserves their instructions, jumps and local-variable slots. It remaps native class aliases and the merged self-owner to small test classes, attaches the expected MixinMerged provenance, and builds an executable post-Mixin callback/cancellation shape. The callback implementation is the real vendored Mixin class, rather than a test replacement. The baseline proves the author handler cancels native init and constructs its screen; the production transformer then permits native init, unrelated callbacks and one publication, while leaving normal WorldOptions unrequested.

This is an offline executable check of the specified post-Mixin shape and actual author handler semantics. It does not execute the Mixin engine, Minecraft, rendering, Steam networking or two clients. The separate WorldOptions fixture tests the explicit pause-button entry and native screen supplier; this joint fixture marks the same request registry to isolate the merged-init boundary. No missing dependency or download is silently skipped: all required inputs are pinned in the repository.
