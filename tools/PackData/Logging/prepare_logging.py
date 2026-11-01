"""Prepare or verify pack-owned network logging at source-authoring time only.

Copy this standalone file to a pack as tools/network_logging_policy.py. The
publisher calls verify(pack); it must never call prepare during publication or
from the launcher. No game, process, network, or launcher dependency is used.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import stat
import tempfile
from pathlib import Path
import xml.etree.ElementTree as ET

CONFIG = "config/log4j2.xml"
JVM = "launcher/jvm-args.txt"
OWNED = "launcher/pack-owned.txt"
RECEIPT = "tools/network-logging-policy.json"
SELECTOR = "-Dlog4j.configurationFile=config/log4j2.xml"
LOGGER = "net.minecraft.network.Connection"
MESSAGES = ["Failed to sent packet", "Double fault", "Skipping packet due to errors", "Timeout"]
MESSAGE_PATTERN = "^(" + "|".join(MESSAGES) + ")$"
SUPPORTED_MINECRAFT = {"1.20.1", "1.21.1", "26.3"}
REFERENCE_SHA256 = "9e7b9918b6ede54f7393ed3d307e51a2edb032b98c12f069f5df976bcf940b89"
BLOCK = '''        <!-- Preserve only Connection's caught throwable diagnostics, without its packet DEBUG stream. -->
        <Logger level="DEBUG" name="net.minecraft.network.Connection">
            <Filters>
                <ThresholdFilter level="INFO" onMatch="ACCEPT" onMismatch="NEUTRAL"/>
                <RegexFilter regex="^(Failed to sent packet|Double fault|Skipping packet due to errors|Timeout)$" onMatch="NEUTRAL" onMismatch="DENY"/>
            </Filters>
        </Logger>
'''


def _require(condition: bool, message: str):
    if not condition:
        raise ValueError(message)


def _path(root: Path, relative: str) -> Path:
    root = root.absolute()
    result = root / relative
    _require(result.absolute().is_relative_to(root), "Logging path escaped source pack")
    current = result
    while True:
        if current.exists() or current.is_symlink():
            _require(not current.is_symlink() and not current.is_junction() and
                     not (current.lstat().st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT
                          if hasattr(current.lstat(), "st_file_attributes") else False),
                     "Logging source paths may not pass through links: " + str(current))
        if current.parent == current:
            break
        current = current.parent
    return result


def _read(root: Path, relative: str, optional=False) -> bytes | None:
    path = _path(root, relative)
    if optional and not path.exists():
        return None
    _require(path.is_file(), "Missing pack logging file: " + relative)
    _require(path.stat().st_size <= 1024 * 1024, "Pack logging metadata is too large: " + relative)
    return path.read_bytes()


def _text(data: bytes) -> str:
    try:
        return data.decode("utf-8-sig")
    except UnicodeDecodeError as error:
        raise ValueError("Pack logging source must use UTF-8") from error


def _encode(text: str, original: bytes) -> bytes:
    return (b"\xef\xbb\xbf" if original.startswith(b"\xef\xbb\xbf") else b"") + text.encode("utf-8")


def _lines(data: bytes | None) -> list[str]:
    return [line.strip() for line in _text(data or b"").splitlines()
            if line.strip() and not line.strip().startswith("#")]


def _selector(data: bytes | None, optional=False):
    selected = [line for line in _lines(data) if line.startswith("-Dlog4j.configurationFile")]
    _require((optional and not selected) or selected == [SELECTOR],
             "Expected one exact pack JVM logging selector; refusing conflicting or duplicate choices")
    return bool(selected)


def _owns(data: bytes | None) -> bool:
    return any(line.lower() in {CONFIG.lower(), "config/**"} for line in _lines(data))


def _append(data: bytes | None, line: str) -> bytes:
    data = data or b""
    text = _text(data)
    newline = "\r\n" if "\r\n" in text else "\n"
    return _encode(text + ("" if not text or text.endswith(("\n", "\r")) else newline) + line + newline, data)


def _xml(data: bytes, allow_missing=False) -> bool:
    text = _text(data)
    _require("<!DOCTYPE" not in text.upper() and "<!ENTITY" not in text.upper(), "Unsupported logging XML declaration")
    try:
        root = ET.fromstring(text)
    except ET.ParseError as error:
        raise ValueError("Invalid pack logging XML") from error
    _require(root.tag == "Configuration", "Unsupported logging configuration root")
    _require(root.find("Filters") is None, "Unsupported global logging filter collision")
    loggers = root.findall("Loggers")
    _require(len(loggers) == 1, "Expected one Loggers section")
    native = loggers[0].findall("Root")
    _require(len(native) == 1 and native[0].get("level", "").upper() == "INFO", "Root logging must remain INFO")
    refs = native[0].findall("AppenderRef")
    _require(all(child.tag == "AppenderRef" and not list(child) for child in native[0]), "Unsupported Root filter collision")
    _require(sum(ref.get("ref") == "File" for ref in refs) == 1 and
             all(ref.get("level", "").upper() in {"", "ALL", "TRACE", "DEBUG"} for ref in refs if ref.get("ref") == "File"),
             "Root must retain File DEBUG diagnostics")
    _require(sum(ref.get("ref") == "SysOut" for ref in refs) == 1 and
             all(ref.get("level", "").upper() == "INFO" for ref in refs if ref.get("ref") in {"SysOut", "ServerGuiConsole"}),
             "Console and GUI logging must remain INFO")
    files = root.findall("Appenders/RollingRandomAccessFile")
    files = [item for item in files if item.get("name") == "File"]
    _require(len(files) == 1 and files[0].get("fileName") == "logs/latest.log", "File must write logs/latest.log")
    _require(all(child.tag in {"PatternLayout", "Policies", "DefaultRolloverStrategy"} for child in files[0]),
             "Unsupported File filter collision")
    appenders = root.findall("Appenders/*")
    _require(len({item.get("name") for item in appenders}) == len(appenders) and
             len(root.findall("Appenders/Console[@name='SysOut']")) == 1, "Expected unique appenders and native SysOut console")
    layout = files[0].find("PatternLayout")
    _require(layout is not None and re.search(r"%(?:tEx|xEx|ex|throwable)", layout.get("pattern", "")),
             "File layout must retain exception stacks")
    size = files[0].find("Policies/SizeBasedTriggeringPolicy")
    strategy = files[0].find("DefaultRolloverStrategy")
    _require(size is not None and strategy is not None, "File rollover must remain bounded")
    match = re.fullmatch(r"\s*(\d+)\s*(B|KB|MB)\s*", size.get("size", ""), re.IGNORECASE)
    _require(match is not None and 0 < int(match[1]) * {"B": 1, "KB": 1024, "MB": 1024 * 1024}[match[2].upper()] <= 32 * 1024 * 1024,
             "File rollover size must be at most 32 MB")
    _require(strategy.get("max", "").isdigit() and 1 <= int(strategy.get("max", "0")) <= 5, "File rollover count must be bounded to five")
    for appender in root.findall("Appenders/*"):
        _require("debug" not in Path(appender.get("fileName", "")).name.lower() and
                 "debug" not in Path(appender.get("filePattern", "")).name.lower(), "A separate DEBUG file is outside this policy")
    entries = loggers[0].findall("Logger")
    selected = [item for item in entries if item.get("name") == LOGGER]
    _require(len(selected) <= 1, "Duplicate native Connection logger")
    for item in entries:
        if item.get("name", "").startswith("net.minecraft.network") and item.get("name") != LOGGER:
            _require(item.get("level", "").upper() in {"INFO", "WARN", "ERROR", "FATAL", "OFF"}, "Unbounded network logger collision")
    if not selected:
        _require(allow_missing, "Missing bounded native Connection logger")
        return False
    logger = selected[0]
    _require(logger.get("level", "").upper() == "DEBUG" and logger.get("additivity", "true").lower() == "true" and
             [child.tag for child in logger] == ["Filters"], "Unsupported native Connection logger collision")
    filters = logger[0]
    _require(not filters.attrib and [(child.tag, child.attrib) for child in filters] == [
        ("ThresholdFilter", {"level": "INFO", "onMatch": "ACCEPT", "onMismatch": "NEUTRAL"}),
        ("RegexFilter", {"regex": MESSAGE_PATTERN, "onMatch": "NEUTRAL", "onMismatch": "DENY"})],
        "Native Connection DEBUG must admit only the four approved exception labels")
    _require(all(not list(child) for child in filters), "Unexpected nested logging filter")
    return True


def _version(pack: Path) -> str:
    try:
        value = json.loads(_text(_read(pack, "portable-pack.json")))["minecraftVersion"]
    except (KeyError, TypeError, json.JSONDecodeError) as error:
        raise ValueError("Missing source Minecraft version") from error
    _require(value in SUPPORTED_MINECRAFT, "Network logging native proof does not cover Minecraft " + str(value))
    return value


def _receipt(config: bytes, version: str) -> dict:
    return {"schemaVersion": 1, "kind": "pack-owned-bounded-network-logging", "minecraftVersion": version,
            "config": {"path": CONFIG, "sizeBytes": len(config), "sha256": hashlib.sha256(config).hexdigest()},
            "jvmSelector": SELECTOR, "ownership": CONFIG, "logger": LOGGER, "debugMessages": list(MESSAGES),
            "referenceConfigSha256": REFERENCE_SHA256}


def verify(pack: Path) -> dict:
    """Verify source logging and its pinned receipt without writing any files."""
    pack = Path(pack).absolute()
    config = _read(pack, CONFIG)
    _xml(config)
    _selector(_read(pack, JVM))
    _require(_owns(_read(pack, OWNED)), "Pack must own config/log4j2.xml explicitly or through config/**")
    try:
        receipt = json.loads(_text(_read(pack, RECEIPT)))
    except json.JSONDecodeError as error:
        raise ValueError("Invalid network logging policy receipt") from error
    expected = _receipt(config, _version(pack))
    _require(receipt == expected, "Network logging receipt differs; reapply the source policy and review updated pins")
    return expected


def _atomic(path: Path, data: bytes):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(prefix=".logging-", dir=path.parent, delete=False) as output:
        temporary = Path(output.name)
        output.write(data)
        output.flush()
        os.fsync(output.fileno())
    try:
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def prepare(pack: Path, template: Path | None = None) -> dict:
    """Apply this policy to an authored source checkout, preserving other bytes."""
    pack = Path(pack).absolute()
    for marker in (".portable-instance.json", ".portable-pack-sync.json", "portable-pack-source.json"):
        _require(not _path(pack, marker).exists(), "Refusing an installed/live pack; use an independent source checkout")
    version = _version(pack)
    original = {relative: _read(pack, relative, optional=True) for relative in (CONFIG, JVM, OWNED, RECEIPT)}
    config = original[CONFIG]
    if config is None:
        _require(template is not None and Path(template).is_file(), "A new pack requires an explicit reviewed --template")
        config = Path(template).read_bytes()
        _require(len(config) <= 1024 * 1024, "Logging template is too large")
    if not _xml(config, allow_missing=True):
        text = _text(config)
        starts = list(re.finditer(r"(?m)^[ \t]*<Root\b", text))
        _require(len(starts) == 1, "Unsupported authored Root formatting; preserve XML for review")
        newline = "\r\n" if "\r\n" in text else "\n"
        text = text[:starts[0].start()] + BLOCK.replace("\n", newline) + text[starts[0].start():]
        config = _encode(text, config)
    _xml(config)
    jvm = original[JVM] or b""
    if not _selector(jvm, optional=True):
        jvm = _append(jvm, SELECTOR)
    owned = original[OWNED] or b""
    if not _owns(owned):
        owned = _append(owned, CONFIG)
    receipt = _receipt(config, version)
    outputs = {CONFIG: config, JVM: jvm, OWNED: owned,
               RECEIPT: (json.dumps(receipt, indent=2, ensure_ascii=False) + "\n").encode("utf-8")}
    changed = []
    try:
        for relative, data in outputs.items():
            if original[relative] != data:
                _atomic(_path(pack, relative), data)
                changed.append(relative)
        verify(pack)
    except Exception:
        for relative in reversed(changed):
            previous = original[relative]
            if previous is None:
                _path(pack, relative).unlink(missing_ok=True)
            else:
                _atomic(_path(pack, relative), previous)
        raise
    return {"changed": changed, "policy": receipt}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("prepare", "verify"))
    parser.add_argument("--pack", required=True, type=Path)
    parser.add_argument("--template", type=Path, help="Explicit default XML for a new source pack only")
    args = parser.parse_args()
    try:
        result = prepare(args.pack, args.template) if args.operation == "prepare" else verify(args.pack)
    except (ValueError, OSError) as error:
        parser.exit(1, "error: " + str(error) + "\n")
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
