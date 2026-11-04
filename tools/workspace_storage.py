#!/usr/bin/env python3
"""Measure temporary workspace data without deleting or changing any files.

Run from anywhere: python tools/workspace_storage.py --check --budget-gib 50
Exit codes: 0 = measured/within budget, 1 = over budget, 2 = incomplete scan.
"""

import argparse
import ctypes
import json
import math
import os
from pathlib import Path
import shutil
import stat


TEMP_ROOTS = (
    'artifacts/agent-runs',
    'Program.Tests/PortabilityAudit/Artifacts',
    'Program.Tests/TestResults',
    '.playwright-mcp',
    'Program/bin',
    'Program/obj',
    'Program/Build',
    'Program/publish',
    'Program.Tests/bin',
    'Program.Tests/obj',
)
GIB = 1024 ** 3
REPARSE_POINT = 0x400
SPARSE_OR_COMPRESSED = 0x200 | 0x800


def long_path(path):
    value = os.path.abspath(path)
    if os.name != 'nt' or value.startswith('\\\\?\\'):
        return value
    if value.startswith('\\\\'):
        return '\\\\?\\UNC\\' + value[2:]
    return '\\\\?\\' + value


def is_link(info):
    return stat.S_ISLNK(info.st_mode) or bool(
        getattr(info, 'st_file_attributes', 0) & REPARSE_POINT
    )


def data_bytes(path, info):
    """Exclude sparse holes/compression, without reading the file contents."""
    if os.name == 'nt' and getattr(info, 'st_file_attributes', 0) & SPARSE_OR_COMPRESSED:
        high = ctypes.c_ulong()
        ctypes.set_last_error(0)
        low = _compressed_size(long_path(path), ctypes.byref(high))
        if low == 0xFFFFFFFF and ctypes.get_last_error():
            raise ctypes.WinError(ctypes.get_last_error())
        return (high.value << 32) | low
    return info.st_size


if os.name == 'nt':
    _kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
    _compressed_size = _kernel32.GetCompressedFileSizeW
    _compressed_size.argtypes = [ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_ulong)]
    _compressed_size.restype = ctypes.c_ulong


def scan_root(project, relative, global_files):
    root = project.joinpath(*relative.split('/'))
    base = os.path.normcase(os.path.abspath(project))
    assert os.path.commonpath([base, os.path.normcase(os.path.abspath(root))]) == base
    result = dict(path=relative, exists=False, logical_bytes=0, unique_bytes=0,
                  files=0, skipped_links=0, error_count=0, errors=[])
    local_files = set()
    new_global_bytes = 0

    def record_error(path, exc):
        result['error_count'] += 1
        if len(result['errors']) < 5:
            result['errors'].append({'path': str(path), 'error': str(exc)})

    # Check each component so an intermediate junction cannot escape the project.
    cursor = project
    try:
        for part in relative.split('/'):
            cursor = cursor / part
            info = os.stat(long_path(cursor), follow_symlinks=False)
            if is_link(info):
                result['skipped_links'] += 1
                return result, 0
            if not stat.S_ISDIR(info.st_mode):
                raise NotADirectoryError(str(cursor))
    except FileNotFoundError:
        return result, 0
    except OSError as exc:
        record_error(cursor, exc)
        return result, 0
    result['exists'] = True

    pending = [root]
    while pending:
        directory = pending.pop()
        try:
            with os.scandir(long_path(directory)) as entries:
                for entry in entries:
                    try:
                        # Full stat is required: DirEntry.stat can return zero file IDs on Windows.
                        info = os.stat(entry.path, follow_symlinks=False)
                        if is_link(info):
                            result['skipped_links'] += 1
                        elif stat.S_ISDIR(info.st_mode):
                            pending.append(Path(entry.path))
                        elif stat.S_ISREG(info.st_mode):
                            result['files'] += 1
                            result['logical_bytes'] += info.st_size
                            identity = ((info.st_dev, info.st_ino) if info.st_ino else
                                        ('path', os.path.normcase(entry.path)))
                            if identity in local_files:
                                continue
                            size = data_bytes(entry.path, info)
                            local_files.add(identity)
                            result['unique_bytes'] += size
                            if identity not in global_files:
                                global_files.add(identity)
                                new_global_bytes += size
                    except OSError as exc:
                        record_error(entry.path, exc)
        except OSError as exc:
            record_error(directory, exc)
    return result, new_global_bytes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', type=Path, default=Path(__file__).resolve().parents[1],
                        help='Project directory (defaults to this script\'s repository).')
    parser.add_argument('--budget-gib', type=float, default=50,
                        help='Maximum combined unique temporary data in GiB (default: 50).')
    parser.add_argument('--check', action='store_true', help='Fail if over budget or scan is incomplete.')
    args = parser.parse_args()
    if not math.isfinite(args.budget_gib) or args.budget_gib < 0:
        parser.error('--budget-gib must be a finite nonnegative number')
    project = Path(os.path.abspath(args.project))
    try:
        info = os.stat(long_path(project), follow_symlinks=False)
        if is_link(info) or not stat.S_ISDIR(info.st_mode):
            parser.error('--project must be an ordinary directory, not a link or junction')
    except OSError as exc:
        parser.error(str(exc))

    global_files = set()
    roots = []
    unique_bytes = 0
    for relative in TEMP_ROOTS:
        result, added = scan_root(project, relative, global_files)
        roots.append(result)
        unique_bytes += added
    budget_bytes = int(args.budget_gib * GIB)
    complete = all(root['error_count'] == 0 for root in roots)
    report = dict(project=str(project), roots=roots, unique_bytes=unique_bytes,
                  logical_bytes=sum(root['logical_bytes'] for root in roots),
                  unique_gib=round(unique_bytes / GIB, 3),
                  free_disk_bytes=shutil.disk_usage(long_path(project)).free,
                  budget_gib=args.budget_gib, budget_bytes=budget_bytes,
                  within_budget=unique_bytes <= budget_bytes, complete=complete)
    print(json.dumps(report, ensure_ascii=True, indent=2))
    if args.check and not complete:
        return 2
    return 1 if args.check and unique_bytes > budget_bytes else 0


if __name__ == '__main__':
    raise SystemExit(main())
