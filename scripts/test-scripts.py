#!/usr/bin/env python3
"""Contract tests for shell argument handling; launches no market/benchmark jobs."""
import os
from pathlib import Path
import shlex
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUST = Path(os.environ.get('RUST_ROOT', str(Path.home() / 'CLionProjects/assumption-zero')))


class Scripts(unittest.TestCase):
    def run_script(self, name, *args, ok=True):
        path = RUST / 'scripts' / name if name == 'run-rust.sh' else ROOT / 'scripts' / name
        env = dict(os.environ, CSHARP_ROOT=str(ROOT), RUST_ROOT=str(RUST))
        env.pop('APP_CPUS', None)
        env.pop('SIMULATOR_CPUS', None)
        p = subprocess.run([str(path), *args, '--dry-run'], cwd='/tmp', env=env,
                           text=True, capture_output=True)
        self.assertEqual(p.returncode == 0, ok, p.stderr)
        return shlex.split(p.stdout)

    def test_default_300000_for_both_languages(self):
        for script in ('run-csharp.sh', 'run-rust.sh'):
            cmd = self.run_script(script, 'benchmark')
            self.assertEqual(cmd[cmd.index('--max-messages') + 1], '300000')
            self.assertEqual(cmd[cmd.index('--target-duration-seconds') + 1], '60')

    def test_limit_override_and_all(self):
        cmd = self.run_script('run-simulator.sh', 'compare', '--max-messages', '123')
        self.assertEqual(cmd[cmd.index('--max-messages') + 1], '123')
        cmd = self.run_script('run-simulator.sh', 'compare', '--all')
        self.assertIn('--all', cmd)
        self.assertNotIn('--max-messages', cmd)

    def test_smoke_defaults_and_override(self):
        cmd = self.run_script('run-rust.sh', 'smoke')
        self.assertEqual(cmd[cmd.index('--max-messages') + 1], '1000')
        self.assertIn('--smoke', cmd)
        cmd = self.run_script('run-simulator.sh', 'compare', '--smoke', '--max-messages', '120')
        self.assertEqual(cmd[cmd.index('--max-messages') + 1], '120')

    def test_rejects_conflicts_and_missing_values(self):
        for args in [('--all', '--max-messages', '5'), ('--max-messages', '0'),
                     ('--max-messages',), ('--max-messages', '-1'),
                     ('--speed', '1', '--target-duration-seconds', '60'),
                     ('--unknown',), ('--all', '--all')]:
            self.run_script('run-csharp.sh', 'benchmark', *args, ok=False)

    def test_paths_with_spaces_are_single_arguments(self):
        cmd = self.run_script('run-rust.sh', 'benchmark', '--out', '/tmp/results with spaces',
                              '--rust-app', '/tmp/rust with spaces')
        self.assertIn('/tmp/results with spaces', cmd)
        self.assertIn('/tmp/rust with spaces', cmd)

    def test_manual_limits_belong_to_simulator(self):
        self.run_script('run-rust.sh', 'replay', '--max-messages', '1000', ok=False)
        cmd = self.run_script('run-simulator.sh', 'replay', '--all', '--speed', '1')
        self.assertIn('--all', cmd)
        self.assertEqual(cmd[cmd.index('--speed') + 1], '1')

    def test_build_is_separate(self):
        self.assertIn('build', self.run_script('run-rust.sh', 'build'))
        self.assertNotIn('build', self.run_script('run-rust.sh', 'benchmark'))


if __name__ == '__main__':
    unittest.main()
