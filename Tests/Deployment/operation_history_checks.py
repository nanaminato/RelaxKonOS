"""Exercise the Windows launcher's clear entry point in an isolated journal."""
import base64
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / 'deployment/launcher/RelaxKonOS-Deploy.ps1').read_text(encoding='utf-8-sig')
BLOCK = SOURCE.split('if ($ClearOperationId) {', 1)[1].split('if ($DiagnosticsOperationId)', 1)[0]
BLOCK = 'if ($ClearOperationId) {' + BLOCK
ID = '12345678-1234-1234-1234-123456789abc'


class OperationHistoryChecks(unittest.TestCase):
    def exercise(self, state, expected, operation_id=ID, locked=False):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            if state is not None:
                (root / (ID + '.json')).write_text(json.dumps({'operationId': operation_id, 'state': state}))
            for extension in ('.log', '.jsonl', '.digest'):
                (root / (ID + extension)).write_text('retained test content')
            setup = r'''
$ErrorActionPreference = 'Stop'
$ClearOperationId = '12345678-1234-1234-1234-123456789abc'
$script:record = @{ operationId = '' }
$operationsRoot = $env:HISTORY_TEST_ROOT
$lockPath = Join-Path $operationsRoot 'deploy.lock'
function Initialize-Journal {}
function ConvertFrom-StrictJsonObject($text) { return ConvertFrom-Json $text }
function Get-OperationRecordPath { Join-Path $operationsRoot ($script:record.operationId + '.json') }
function Get-OperationDiagnosticsPath { Join-Path $operationsRoot ($script:record.operationId + '.log') }
function Get-OperationEventsPath { Join-Path $operationsRoot ($script:record.operationId + '.jsonl') }
'''
            if locked:
                setup += "$held = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')\n"
            encoded = base64.b64encode((setup + BLOCK).encode('utf-16-le')).decode()
            result = subprocess.run([shutil.which('pwsh') or 'powershell.exe', '-NoProfile', '-NonInteractive',
                                     '-EncodedCommand', encoded], env={**os.environ, 'HISTORY_TEST_ROOT': directory},
                                    capture_output=True)
            self.assertEqual(expected, result.returncode, result.stderr.decode(errors='replace'))
            self.assertTrue((root / (ID + '.digest')).exists())
            for extension in ('.log', '.jsonl'):
                self.assertEqual(expected != 0, (root / (ID + extension)).exists())
            if state is not None:
                self.assertEqual(expected != 0, (root / (ID + '.json')).exists())

    def test_completed_records_and_logs_are_removed_but_replay_digest_survives(self):
        for state in ('succeeded', 'failed', 'cancelled', 'interrupted'):
            with self.subTest(state=state):
                self.exercise(state, 0)

    def test_running_unknown_missing_and_mismatched_records_are_preserved(self):
        for state in ('running', 'queued', 'unknown'):
            with self.subTest(state=state):
                self.exercise(state, 65)
        self.exercise(None, 66)
        self.exercise('succeeded', 65, operation_id='other-operation')

    def test_active_writer_blocks_clear(self):
        self.exercise('succeeded', 75, locked=True)


if __name__ == '__main__':
    unittest.main()
