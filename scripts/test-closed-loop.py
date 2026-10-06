#!/usr/bin/env python3
import importlib.util
from pathlib import Path
import subprocess
import unittest
import sys
sys.dont_write_bytecode = True

SCRIPT=Path(__file__).with_name('run-closed-loop-benchmark.sh')
spec=importlib.util.spec_from_file_location('launcher',Path(__file__).with_name('closed-loop-benchmark.py'))
launcher=importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)

class LauncherTests(unittest.TestCase):
    def run_command(self,*args):
        return subprocess.run([str(SCRIPT),*args],capture_output=True,text=True)
    def test_coordinator_and_children_share_selected_dotnet_host(self):
        from unittest.mock import patch
        with patch.dict(launcher.os.environ, {'DOTNET_HOST_PATH': '/old/dotnet', 'HOME': '/root'}):
            env=launcher.runtime_environment('/selected/dotnet')
            self.assertEqual('/selected/dotnet',env['DOTNET_HOST_PATH'])
            self.assertEqual('/old/dotnet',launcher.os.environ['DOTNET_HOST_PATH'])
            self.assertEqual('/root',env['HOME'])
    def test_defaults_and_no_credentials(self):
        r=self.run_command('run','--dry-run')
        self.assertEqual(0,r.returncode,r.stderr)
        for s in ['--max-messages 500000','--target-duration-seconds 600','--repetitions 5']:
            self.assertIn(s,r.stdout)
        self.assertNotIn('Password',r.stdout+r.stderr)
    def test_check_is_deterministic_only(self):
        r=self.run_command('check','--dry-run','--max-messages','1000')
        self.assertEqual(0,r.returncode,r.stderr)
        self.assertIn('--check-only true',r.stdout)
    def test_invalid_workloads_fail(self):
        for args in [('run','--max-messages','0'),('run','--repetitions','0'),('run','--target-duration-seconds','nan'),('run','--nonsense')]:
            self.assertNotEqual(0,self.run_command(*args,'--dry-run').returncode)
    def test_affinity_overlap_fails(self):
        cpu=str(min(launcher.os.sched_getaffinity(0)))
        r=self.run_command('run','--app-cpus',cpu,'--simulator-cpus',cpu,'--dry-run')
        self.assertNotEqual(0,r.returncode)
        self.assertIn('physical cores',r.stderr)
    def test_out_of_range_cpu_fails(self):
        self.assertNotEqual(0,self.run_command('run','--app-cpus','999999','--dry-run').returncode)

if __name__=='__main__': unittest.main()
