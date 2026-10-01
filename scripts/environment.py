#!/usr/bin/env python3
"""Read-only affinity validation and build-time provenance snapshots."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
from datetime import datetime, timezone


def cpus(text):
    result = set()
    for item in text.split(','):
        if '-' in item:
            a, b = map(int, item.split('-'))
            if a < 0 or b < a or b > 1_000_000:
                raise ValueError('Invalid CPU range')
            result.update(range(a, b + 1))
        else:
            result.add(int(item))
    if not result or not result <= os.sched_getaffinity(0):
        raise ValueError('CPU list must be nonempty and within the allowed CPUs')
    return result


def physical(cpu):
    root = Path(f'/sys/devices/system/cpu/cpu{cpu}/topology')
    return ((root / 'physical_package_id').read_text().strip(),
            (root / 'core_id').read_text().strip())


def command(*args):
    p = subprocess.run(args, capture_output=True, text=True)
    return p.stdout.strip() if p.returncode == 0 else 'unavailable'


def main():
    if sys.argv[1] == 'affinity':
        app, simulator = map(cpus, sys.argv[2:4])
        if {physical(c) for c in app} & {physical(c) for c in simulator}:
            raise ValueError('Application and simulator share a physical core (including SMT siblings)')
        return
    if sys.argv[1] != 'manifest':
        raise ValueError('Expected affinity or manifest')
    root = Path(sys.argv[2]).resolve()
    digest = hashlib.sha256()
    files = []
    for p in sorted(root.rglob('*')):
        if any(part in {'.git', '.idea', 'bin', 'obj', 'target', 'benchmark-results'} for part in p.relative_to(root).parts):
            continue
        if p.is_file() and (p.suffix in {'.cs', '.csproj', '.rs', '.toml', '.lock', '.xml', '.json'}):
            files.append(p)
    for p in files:
        digest.update(str(p.relative_to(root)).encode() + b'\0' + p.read_bytes() + b'\0')
    data = {'BuiltAtUtc': datetime.now(timezone.utc).isoformat(), 'SourceTreeSha256': digest.hexdigest(),
            'SourceRevision': command('git', '-C', str(root), 'rev-parse', 'HEAD'),
            'SourceStatus': command('git', '-C', str(root), 'status', '--porcelain'),
            'RustFlags': os.environ.get('RUSTFLAGS'), 'Configuration': 'Release'}
    for binary in map(Path, sys.argv[3:]):
        data['BinarySha256'] = hashlib.sha256(binary.read_bytes()).hexdigest()
        Path(str(binary) + '.build.json').write_text(json.dumps(data, indent=2) + '\n')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError) as error:
        sys.exit(str(error))
