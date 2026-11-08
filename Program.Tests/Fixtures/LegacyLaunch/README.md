# Legacy launch profile fixtures

These are complete metadata profiles used by the real production launcher:

- `1.6.4.json`: Mojang 1.6.4 profile, SHA256 `e773d3d7f2a93c2706d1d06ff9574f9df388f9274a9ed131e11e8b05d09dc977`.
- `legacyfabric.json`: Legacy Fabric 0.18.2 for 1.6.4, installed with its separate ID and explicit vanilla JAR, SHA256 `b6851eedc89b2c9f8b303d6c61230958a61e849b5568a62a3766414bcc7f25cf`.

Provenance: the verified local runtime's installed metadata on 2026-11-08.
Upstream sources are Mojang's version manifest and
`https://meta.legacyfabric.net/v2/versions/loader/1.6.4/0.18.2/profile/json`.
No user settings, credentials or world files are included.

Tests use CmlLib's actual BuildProcess with these full profiles and a supplied
native directory, without starting Java or unpacking native libraries. The
baseline reproduces the missing inherited game arguments. Corrected arguments
must preserve session/name, the instance directory, custom options, resolution
and the index-selected virtual assets path. Explicit loader arguments remain
unchanged. This is a process-composition test, not an audio/device acceptance run.
