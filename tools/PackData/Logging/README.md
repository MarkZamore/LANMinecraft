# Pack-owned game logging

`log4j2.xml` is native pack data, not an embedded launcher resource. To adopt
this policy, place its exact bytes at `config/log4j2.xml` in the pack and add
one line to `launcher/jvm-args.txt`:

```text
-Dlog4j.configurationFile=config/log4j2.xml
```

Preserve other JVM arguments and any existing authored logging configuration.
A pack that supplies no logging data keeps its native game's logging setup;
the launcher neither writes a replacement XML nor selects one for it.

This template keeps INFO, WARN and ERROR in `latest.log`, retains loader
diagnostics, bounds rollovers and omits a separate DEBUG file. Its bytes match
the former launcher template. Native Log4j, Mojang queue, formatting and loader
throwable conversion were checked for Minecraft 1.20.1, 1.21.1 and 26.3. This
does not replace a full loader/client run. Newer Log4j versions report the
existing `packages` attribute as deprecated; the template is unchanged.

The launcher's separate diagnostic retention still removes completed
`debug.log` and rolled DEBUG archives after retaining bounded e4steam session
diagnostics. Moving game configuration into packs does not change that cleanup
policy or make diagnostic archives world backups.
