"""Compare bounded ERROR/FATAL signatures within native client/world stages.

This is local evidence processing, never an automatic compatibility PASS.
Resource IDs/caller names remain exact; only object identity and JEI elapsed
seconds are normalized. QA credentials and timing notices stay separate.
"""
from __future__ import annotations

import argparse
import collections
import datetime
import hashlib
import json
from pathlib import Path
import re

LINE = re.compile(r"^\[(?P<time>\d\d:\d\d:\d\d)\] \[(?P<thread>.+)/(?P<level>[A-Z]+)\]: (?P<message>.*)$")
IDENTITY = re.compile(r"(?<=[\w$])@[0-9a-fA-F]{4,16}\b")
JEI_TIME = re.compile(r"(Registering .+ is running and has taken )\d+(?:\.\d+)?( seconds so far)$")
CREDENTIAL_ERRORS = {
    "Failed to verify authentication",
    "Failed to retrieve profile key pair",
    "Couldn't connect to realms",
    "Failed to connect to Realms",
}


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read(path: Path) -> dict:
    phase = "startup"
    stage_times: dict[str, str] = {}
    counts: collections.Counter[tuple[str, str, str]] = collections.Counter()
    first: dict[tuple[str, str, str], str] = {}
    last: dict[tuple[str, str, str], str] = {}
    markers = {"serverStarted": False, "playerJoined": False, "allDimensionsSaved": False, "clientStopping": False}
    with path.open(encoding="utf-8-sig", errors="replace") as stream:
        for line in stream:
            match = LINE.match(line.rstrip("\r\n"))
            if match is None:
                continue
            time, level, message = match["time"], match["level"], match["message"]
            if "Starting integrated minecraft server version" in message:
                phase = "world-load"; markers["serverStarted"] = True; stage_times.setdefault(phase, time)
            if "joined the game" in message:
                phase = "world-ready"; markers["playerJoined"] = True; stage_times.setdefault(phase, time)
            if message == "Stopping server":
                phase = "shutdown"; stage_times.setdefault(phase, time)
            if "All dimensions are saved" in message:
                markers["allDimensionsSaved"] = True
            if message == "Stopping!":
                markers["clientStopping"] = True
            if level not in ("ERROR", "FATAL"):
                continue
            signature = IDENTITY.sub("@<object>", message)
            signature = JEI_TIME.sub(r"\1<elapsed>\2", signature)
            key = (phase, level, signature)
            counts[key] += 1; first.setdefault(key, time); last[key] = time
    return {"counts": counts, "first": first, "last": last, "markers": markers,
            "stageTimes": stage_times, "sha256": sha256(path)}


def category(signature: str, observed_in_baseline: bool) -> str:
    if any(signature.startswith(value) for value in CREDENTIAL_ERRORS):
        return "offline-QA-credentials-or-network"
    if " is running and has taken <elapsed> seconds so far" in signature:
        return "JEI-timing-notice"
    return "observed-baseline-source-path" if observed_in_baseline else "new-runtime-family-unclassified"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--current", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args()
    output = arguments.output.absolute()
    if "artifacts" not in {part.lower() for part in output.parts}:
        raise ValueError("Comparison evidence must stay below artifacts")
    for ancestor in (output, *output.parents):
        if ancestor.is_symlink() or (hasattr(ancestor, "is_junction") and ancestor.is_junction()):
            raise ValueError("Linked evidence paths are forbidden")
    baseline, current = read(arguments.baseline), read(arguments.current)
    rows = []
    known_signatures = {(level, signature) for _, level, signature in baseline["counts"]}
    for key, count in sorted(current["counts"].items()):
        phase, level, signature = key
        known = baseline["counts"].get(key, 0)
        same_signature_elsewhere = (level, signature) in known_signatures
        rows.append({"phase": phase, "level": level, "signature": signature,
                     "category": category(signature, same_signature_elsewhere), "currentCount": count, "baselineSameStageCount": known,
                     "classification": "same-signature-same-stage-baseline" if known else
                         "baseline-signature-different-stage-needs-review" if same_signature_elsewhere else
                         "new-signature-needs-review",
                     "firstCurrentTime": current["first"][key], "lastCurrentTime": current["last"][key]})
    report = {
        "checkedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "baselineSha256": baseline["sha256"], "currentSha256": current["sha256"],
        "baselineMarkers": baseline["markers"], "currentMarkers": current["markers"],
        "baselineStageTimes": baseline["stageTimes"], "currentStageTimes": current["stageTimes"],
        "baselineErrorEventCount": sum(baseline["counts"].values()),
        "currentErrorEventCount": sum(current["counts"].values()),
        "countsAreNotRates": True, "automaticWorldOrSteamPass": False,
        "normalization": ["runtime-object-identity", "JEI-elapsed-seconds-only"],
        "families": rows,
        "newSignatures": [row for row in rows if row["classification"] == "new-signature-needs-review"],
        "differentStageSignatures": [row for row in rows if row["classification"] == "baseline-signature-different-stage-needs-review"],
        "qaAgentDefects": "Check observations.jsonl separately; these are not source-game ERROR events.",
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"newSignatures": len(report["newSignatures"]),
                      "differentStageSignatures": len(report["differentStageSignatures"]),
                      "baselineErrors": report["baselineErrorEventCount"],
                      "currentErrors": report["currentErrorEventCount"], "automaticPass": False}))


if __name__ == "__main__":
    main()
