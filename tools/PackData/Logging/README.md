# Pack-owned game logging

`log4j2.xml` is native pack data, not an embedded launcher resource. The
reviewed template is SHA256
`9e7b9918b6ede54f7393ed3d307e51a2edb032b98c12f069f5df976bcf940b89`.
It belongs at `config/log4j2.xml`; its pack JVM selector is:

```text
-Dlog4j.configurationFile=config/log4j2.xml
```

The bounded Connection logger retains only four native caught-exception DEBUG
labels in the file: `Failed to sent packet`, `Double fault`,
`Skipping packet due to errors`, and `Timeout`. INFO/WARN/ERROR remain intact;
other Connection DEBUG and all TRACE are excluded. Root, Console and GUI stay
at INFO. This records evidence and does not repair a network connection.

This template keeps INFO, WARN and ERROR in `latest.log`, retains loader
diagnostics, bounds rollovers and omits a separate DEBUG file. Native caller
and Log4j proof covers Minecraft 1.20.1, 1.21.1 and 26.3; see `Verification/`.
Forge 1.18.2–40.2.34 and 1.19.2–43.2.10 use the separately verified
`log4j2-forge-1.18-1.19.xml` template for Log4j 2.17.0. It preserves their
inherited Mojang console layout and uses the same four caught-exception labels.
Forge 1.16.5–36.2.34 uses `log4j2-forge-1.16.xml` for the corresponding
NetworkManager logger. Forge 1.12.2–14.23.5.2847 and 14.23.5.2860 use
`log4j2-forge-1.12.xml`, which preserves localized caught-exception text and
filters the observed non-exception DEBUG calls and packet markers.
It does not replace a full loader/client or two-player run. Unsupported native
versions require a reviewed proof/profile extension, rather than an invented
legacy logger. Newer Log4j versions report the retained `packages` attribute
as deprecated.

## Source preparation and future author updates

Copy standalone `prepare_logging.py` into each pack as
`tools/network_logging_policy.py`. Existing source XML and other JVM flags are
preserved; an unsupported logger/configuration collision fails for review.
The helper never rewrites a personal or installed pack and starts no processes
or network activity. Use independent authored source checkouts.

```text
python tools/network_logging_policy.py prepare --pack .
python tools/network_logging_policy.py verify --pack .
```

For a new source without XML, explicitly supply the reviewed template:

```text
python <launcher-source>/tools/PackData/Logging/prepare_logging.py prepare --pack <new-pack-source> --template <launcher-source>/tools/PackData/Logging/log4j2.xml
```

Preparation inserts only the owned logger block, appends the exact selector
once, and claims only `config/log4j2.xml` in `launcher/pack-owned.txt` when an
existing `config/**` claim does not already cover it. Other claims, comments,
encoding, line endings and JVM flags are retained. Existing approved XML is
left byte-identical. Repeated preparation does not rewrite unchanged files.
`tools/network-logging-policy.json` pins the resulting XML size/SHA256,
selector, native version and bounded policy. Update any additional existing
pack support-file locks after reviewing these source changes.

Keep `config/log4j2.xml -text` in the source `.gitattributes` (or an existing
matching `-text` rule). The receipt pins exact bytes, including mixed/CRLF line
endings; Git must not normalize them after preparation. The helper deliberately
does not edit Git attributes. Verify the policy again from a fresh checkout
before publication, alongside the pack's other exact-hash support checks.

The public Python API is `prepare(pack: Path, template: Path | None = None)`
and `verify(pack: Path)`. Both raise `ValueError` for invalid policy. A pack's
manifest generator imports the copied module and calls
`network_logging_policy.verify(pack)` before collecting/publishing files.
Verification writes nothing and must remain mandatory in ordinary generation
and verification modes; publication never calls `prepare`.

On an author update, import the new author tree into an independent source,
reapply this source policy, review/update support-file pins and the receipt,
then run the generator's verification and publish. A missing/reverted XML,
selector, ownership claim or stale receipt prevents silent publication. A new
authored logging configuration is preserved and validated; conflicts require
review. The generic launcher only delivers these pack files and reads their
declarative JVM arguments. No per-pack rule or per-launch XML rewrite is added.

```text
python -B -m unittest discover -s tools/PackData/Logging -p test_logging_policy.py -v
```

The tests exercise custom XML/flag preservation, collisions, missing/duplicate
selectors, bounded filters/rollover, exact ownership, stale receipts, author
updates, idempotence and the copied standalone verification CLI. Synthetic
fixtures are not a native runtime acceptance claim.

The launcher's separate diagnostic retention still removes completed
`debug.log` and rolled DEBUG archives after retaining bounded e4steam session
diagnostics. Moving game configuration into packs does not change that cleanup
policy or make diagnostic archives world backups.
