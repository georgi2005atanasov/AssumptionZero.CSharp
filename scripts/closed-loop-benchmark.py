#!/usr/bin/env python3
"""Build once, then run a quiet-machine controlled exchange comparison."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import platform
import shlex
import subprocess
import sys
import tarfile
import uuid

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def artifacts(rust):
    paths = [rust / 'target/release/assumption-zero']
    for project in ['AssumptionZero.App', 'AssumptionZero.Simulator']:
        folder = ROOT / project / 'bin/Release/net10.0'
        paths.extend(p for p in folder.glob('*') if p.suffix in ('.dll', '.json'))
    paths.extend([rust / 'src/symbols.json', ROOT / 'AssumptionZero.App/Program.cs'])
    return {str(p): digest(p) for p in sorted(set(paths)) if p.is_file()}


def sources(rust):
    result = {}
    for root in [ROOT, rust]:
        for folder, dirs, files in os.walk(root):
            dirs[:] = [d for d in dirs if d not in {'.git', '.idea', 'bin', 'obj', 'target', 'output', 'tmp', 'benchmark-results', 'TestResults'}]
            for name in files:
                p = Path(folder) / name
                if p.suffix in {'.cs', '.rs', '.csproj', '.toml', '.json', '.lock', '.py', '.sh'}:
                    result[str(p)] = digest(p)
    return result


def cpu_set(value):
    cpus = set()
    for part in value.split(','):
        ends = part.split('-')
        if len(ends) == 1:
            cpus.add(int(part))
        elif len(ends) == 2 and int(ends[0]) <= int(ends[1]):
            cpus.update(range(int(ends[0]), int(ends[1]) + 1))
        else:
            raise ValueError('Invalid CPU list')
    if not cpus or not cpus <= os.sched_getaffinity(0):
        raise ValueError('CPU selection is empty or outside the current allowed CPUs')
    return cpus


def physical(cpus):
    result = set()
    for cpu in cpus:
        top = Path(f'/sys/devices/system/cpu/cpu{cpu}/topology')
        result.add(((top / 'physical_package_id').read_text().strip(), (top / 'core_id').read_text().strip()))
    return result


def runtime_environment(dotnet):
    # The coordinator launches C# children. Use the same host that was selected
    # for it, including when HOME or DOTNET_HOST_PATH differs between users.
    return dict(os.environ, DOTNET_HOST_PATH=dotnet)


def main():
    p = argparse.ArgumentParser(description='Local simulated exchange. Defaults: 500k frames, 50k warmup, 600 seconds per paced replay, five alternating pairs plus a timing-disabled baseline pair. About two hours plus deterministic preflight. No Binance access.')
    p.add_argument('mode', choices=['prepare', 'run', 'check'])
    p.add_argument('--max-messages', type=int, default=500000)
    p.add_argument('--target-duration-seconds', type=float, default=600)
    p.add_argument('--repetitions', type=int, default=5)
    p.add_argument('--recording', default='fc401a92-7803-486e-8940-5db1eebc790c')
    p.add_argument('--scenario', choices=['mixed', 'rest'], default='mixed')
    p.add_argument('--rust-root', type=Path, default=Path(os.environ.get('RUST_ROOT', '/home/sparrow/CLionProjects/assumption-zero')))
    p.add_argument('--app-cpus', default=os.environ.get('APP_CPUS'))
    p.add_argument('--simulator-cpus', default=os.environ.get('SIMULATOR_CPUS'))
    p.add_argument('--out', type=Path)
    p.add_argument('--dry-run', action='store_true')
    a = p.parse_args()
    import math
    if a.max_messages < 100 or a.repetitions < 1 or not math.isfinite(a.target_duration_seconds) or a.target_duration_seconds <= 0:
        p.error('Invalid workload parameters')
    rust = a.rust_root.resolve()
    app_cpus = cpu_set(a.app_cpus) if a.app_cpus else set()
    simulator_cpus = cpu_set(a.simulator_cpus) if a.simulator_cpus else set()
    if app_cpus and simulator_cpus and physical(app_cpus) & physical(simulator_cpus):
        p.error('Application and simulator CPUs must not share physical cores (including SMT siblings)')
    dotnet = os.environ.get('DOTNET_HOST_PATH', str(Path.home() / '.dotnet/dotnet'))
    stamp = ROOT / 'benchmark-results/closed-loop-prepared.json'
    if a.mode == 'prepare':
        commands = [([dotnet, 'build', 'AssumptionZero.sln', '-c', 'Release'], ROOT),
                    ([str(Path.home() / '.cargo/bin/cargo'), '+stable', 'build', '--release', '--locked', '--offline'], rust)]
        for cmd, cwd in commands:
            print(shlex.join(cmd), flush=True)
            if not a.dry_run:
                subprocess.run(cmd, cwd=cwd, check=True, env=runtime_environment(dotnet))
        if not a.dry_run:
            stamp.parent.mkdir(parents=True, exist_ok=True)
            stamp.write_text(json.dumps({'artifacts': artifacts(rust), 'sources': sources(rust)}, indent=2))
            print('Prepared. Close other applications, then use run. Preparation does not run the benchmark.')
        return
    name = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '-closed-loop-' + uuid.uuid4().hex[:6]
    output = (a.out or ROOT / 'benchmark-results' / name).resolve()
    cmd = [dotnet, str(ROOT / 'AssumptionZero.Simulator/bin/Release/net10.0/AssumptionZero.Simulator.dll'), 'closed-loop',
           '--max-messages', str(a.max_messages), '--target-duration-seconds', str(a.target_duration_seconds),
           '--repetitions', str(a.repetitions), '--recording', a.recording, '--scenario', a.scenario,
           '--rust-root', str(rust), '--out', str(output)]
    if a.app_cpus:
        cmd += ['--app-cpus', a.app_cpus]
    if a.mode == 'check':
        cmd += ['--check-only', 'true']
    if a.simulator_cpus:
        cmd = ['taskset', '--cpu-list', a.simulator_cpus] + cmd
    print(shlex.join(cmd), flush=True)
    if a.dry_run:
        return
    if not stamp.is_file():
        p.error('Run prepare first')
    prepared = json.loads(stamp.read_text())
    if prepared != {'artifacts': artifacts(rust), 'sources': sources(rust)}:
        p.error('Sources or binaries changed since preparation. Run prepare again before measuring')
    if output.exists() and any(output.iterdir()):
        p.error('Output directory must be empty')
    # The coordinator owns directory creation. Save a provenance sidecar after it exits,
    # including on failure, so incomplete runs cannot be mistaken for accepted results.
    provenance = dict(prepared, command=cmd, system=platform.uname()._asdict(),
                      allowed_cpus=sorted(os.sched_getaffinity(0)), app_cpus=sorted(app_cpus), simulator_cpus=sorted(simulator_cpus))
    provenance['dotnet_info'] = subprocess.check_output([dotnet, '--info'], text=True)
    provenance['rust_build'] = subprocess.check_output([str(rust / 'target/release/assumption-zero'), '--version'], text=True).strip()
    for label, repo in [('csharp', ROOT), ('rust', rust)]:
        provenance[label + '_revision'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip()
        provenance[label + '_status'] = subprocess.check_output(['git', 'status', '--porcelain'], cwd=repo, text=True)
    try:
        completed = subprocess.run(cmd, cwd=ROOT, env=runtime_environment(dotnet))
    except KeyboardInterrupt:
        completed = subprocess.CompletedProcess(cmd, 130)
    if output.exists():
        provenance['exit_code'] = completed.returncode
        (output / 'provenance.json').write_text(json.dumps(provenance, indent=2))
        archive = output.with_name(output.name + ('.tar.gz' if completed.returncode == 0 else '-FAILED.tar.gz'))
        with tarfile.open(archive, 'x:gz') as tar:
            tar.add(output, arcname=output.name)
        print('Results archive:', archive, flush=True)
    sys.exit(completed.returncode)


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as e:
        sys.exit(f'Error: {e}')
