import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class CompiledBridgeTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt" and os.environ.get("KSP_ROOT"),
                         "requires Windows, KSP_ROOT and a freshly built plugin")
    def test_actual_bridge_http_without_unity_startup(self):
        root = Path(__file__).resolve().parents[1]
        compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
        managed = Path(os.environ["KSP_ROOT"]) / "KSP_x64_Data/Managed"
        dll = root / "ksp-plugin/GameData/KspMcp/Plugins/KspMcpBridge.dll"
        with tempfile.TemporaryDirectory(prefix="ksp-http-test-") as directory:
            exe = Path(directory) / "http-test.exe"
            built = subprocess.run([str(compiler), "/nologo", "/out:" + str(exe),
                                    str(root / "tests/csharp/BridgeHttpTests.cs")],
                                   capture_output=True, timeout=30)
            self.assertEqual(built.returncode, 0, built.stdout + built.stderr)
            ran = subprocess.run([str(exe), str(dll), str(managed)], capture_output=True, timeout=30)
            self.assertEqual(ran.returncode, 0, ran.stdout + ran.stderr)
            self.assertIn(b"queued timeout passed", ran.stdout)
