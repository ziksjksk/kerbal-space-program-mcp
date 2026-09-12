"""Check shipped sources and examples without requiring a running game."""

import ast
import json
from pathlib import Path
import shutil
import subprocess
import unittest
import xml.etree.ElementTree as ET

from server.craft_model import validate_craft_document


ROOT = Path(__file__).resolve().parents[1]


class RepositoryIntegrityTests(unittest.TestCase):
    def test_python_sources_parse(self):
        for directory in ("server", "tests"):
            for path in (ROOT / directory).glob("*.py"):
                with self.subTest(path=path.name):
                    ast.parse(path.read_text(encoding="utf-8-sig"), filename=str(path))

    def test_example_craft_documents(self):
        for path in (ROOT / "examples").glob("*.json"):
            with self.subTest(path=path.name):
                document = json.loads(path.read_text(encoding="utf-8-sig"))
                validate_craft_document(document, require_connected=True)

    def test_project_xml(self):
        ET.parse(ROOT / "ksp-plugin" / "KspMcpBridge.csproj")

    def test_project_metadata(self):
        try:
            import tomllib
        except ImportError:
            self.skipTest("TOML parser is in the standard library on Python 3.11+")
        with (ROOT / "pyproject.toml").open("rb") as stream:
            self.assertEqual(tomllib.load(stream)["project"]["name"], "kerbal-space-program-mcp")

    def test_no_shell_diagnostics_in_shipped_sources(self):
        paths = list(ROOT.glob("*.ps1")) + list(ROOT.glob("*.toml"))
        for directory in ("server", "examples", "ksp-plugin"):
            paths.extend((ROOT / directory).rglob("*"))
        for path in paths:
            if path.suffix not in {".py", ".json", ".cs", ".csproj", ".ps1", ".toml", ".cfg"}:
                continue
            with self.subTest(path=str(path.relative_to(ROOT))):
                for line in path.read_text(encoding="utf-8-sig").splitlines():
                    self.assertFalse(line.startswith(("Import-Clixml:", "InvalidOperation:")), line)

    def test_powershell_scripts_parse(self):
        shell = shutil.which("pwsh") or shutil.which("powershell")
        if not shell:
            self.skipTest("PowerShell is not installed")
        script = """
        $failed = $false
        Get-ChildItem -LiteralPath . -Filter *.ps1 -Recurse | ForEach-Object {
            $tokens = $null
            $parseErrors = $null
            $null = [System.Management.Automation.Language.Parser]::ParseFile(
                $_.FullName, [ref]$tokens, [ref]$parseErrors)
            if ($parseErrors.Count) {
                $failed = $true
                $parseErrors | ForEach-Object { Write-Output $_ }
            }
        }
        if ($failed) { exit 1 }
        """
        result = subprocess.run([shell, "-NoProfile", "-NonInteractive", "-Command", script],
                                cwd=ROOT, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
