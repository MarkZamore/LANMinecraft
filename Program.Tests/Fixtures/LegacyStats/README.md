# Native Minecraft 1.6.4 statistics fixture

`stats_btwqahost_unsent.dat` is a 233-byte file written by the real BTW CE 3.1.1
client during the isolated `BtwQaHost` baseline. The baseline then reopened the
game with this native file. `BtwQaHost` is a synthetic QA name; this fixture has
no personal world, Steam account, skin or authentication token.

SHA256: `5a90bcc8cbbc351f8843b352dcdd888dbd26bf5525d7f228f6577ddaa7fe9edc`.

The file's native username, content and checksum are preserved. Tests reuse its
bytes as both `.dat` and `.old` while checking the production instance merge.
This proves byte retention in `GAME/stats`, not native Steam authentication or
cross-machine statistics migration.
