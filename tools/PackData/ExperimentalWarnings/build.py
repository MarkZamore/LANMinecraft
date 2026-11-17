"""Build the deterministic pack-owned experimental confirmation agent."""

from pathlib import Path
import argparse
import hashlib
import json
import os
import subprocess
import zipfile
import tempfile

ROOT = Path(__file__).resolve().parent


def relocate(blob):
    if blob[:4] != b"\xca\xfe\xba\xbe":
        return blob
    count = int.from_bytes(blob[8:10], "big")
    output = bytearray(blob[:10])
    offset = 10
    index = 1
    while index < count:
        tag = blob[offset]
        output.append(tag)
        offset += 1
        if tag == 1:
            size = int.from_bytes(blob[offset : offset + 2], "big")
            offset += 2
            text = blob[offset : offset + size].replace(
                b"org/objectweb/asm", b"lanmc/experiment/asm"
            )
            offset += size
            output += len(text).to_bytes(2, "big") + text
        else:
            size = {
                3: 4,
                4: 4,
                5: 8,
                6: 8,
                7: 2,
                8: 2,
                9: 4,
                10: 4,
                11: 4,
                12: 4,
                15: 3,
                16: 2,
                17: 4,
                18: 4,
                19: 2,
                20: 2,
            }[tag]
            output += blob[offset : offset + size]
            offset += size
            if tag in (5, 6):
                index += 1
        index += 1
    return bytes(output) + blob[offset:]


def build(output, asm, tree, javac):
    pins = json.loads((ROOT / "build-inputs.json").read_text(encoding="utf-8"))
    for name, path in [("asm", asm), ("asm-tree", tree)]:
        blob = path.read_bytes()
        if (
            len(blob) != pins[name]["sizeBytes"]
            or hashlib.sha256(blob).hexdigest() != pins[name]["sha256"]
        ):
            raise ValueError("Unreviewed build input " + str(path))
    with tempfile.TemporaryDirectory(
        prefix="experimental-agent-", dir=output.parent
    ) as temporary:
        classes = Path(temporary)
        subprocess.run(
            [
                str(javac),
                "--release",
                "8",
                "-Xlint:-options",
                "-encoding",
                "UTF-8",
                "-cp",
                os.pathsep.join(map(str, (asm, tree))),
                "-d",
                str(classes),
                str(ROOT / "ExperimentalWarningAgent.java"),
            ],
            check=True,
        )
        files = {
            file.relative_to(classes).as_posix(): file.read_bytes() for file in classes.rglob("*.class")
        }
        for dependency in (asm, tree):
            with zipfile.ZipFile(dependency) as archive:
                files.update(
                    {
                        name: archive.read(name)
                        for name in archive.namelist()
                        if name.endswith(".class") and name != "module-info.class"
                    }
                )
        files = {
            name.replace("org/objectweb/asm", "lanmc/experiment/asm"): relocate(data)
            for name, data in files.items()
        }
        files["META-INF/LICENSE.asm.txt"] = (ROOT / "LICENSE.asm.txt").read_bytes()
        files["META-INF/MANIFEST.MF"] = (
            b"Manifest-Version: 1.0\r\nPremain-Class: lanmc.experiment.ExperimentalWarningAgent\r\nMain-Class: lanmc.experiment.ExperimentalWarningAgent\r\n\r\n"
        )
        with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED) as archive:
            for name, blob in sorted(files.items()):
                info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
                info.create_system = 3
                archive.writestr(info, blob)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--asm", type=Path, required=True)
    parser.add_argument("--tree", type=Path, required=True)
    parser.add_argument("--javac", type=Path, required=True)
    args = parser.parse_args()
    args.out.parent.mkdir(parents=True, exist_ok=True)
    build(args.out, args.asm, args.tree, args.javac)
    print(hashlib.sha256(args.out.read_bytes()).hexdigest(), args.out.stat().st_size)
