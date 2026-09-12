import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class EventBufferTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt", "standalone C# harness uses Windows .NET Framework")
    def test_ring_matches_reference_across_wraparound_and_cursor_limits(self):
        root = Path(__file__).resolve().parents[1]
        compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
        if not compiler.exists():
            self.skipTest(".NET Framework compiler is unavailable")
        with tempfile.TemporaryDirectory(prefix="ksp-event-test-") as directory:
            exe = Path(directory) / "events.exe"
            command = [str(compiler), "/nologo", "/optimize+", "/out:" + str(exe),
                       str(root / "ksp-plugin/src/KspMcpEventBuffer.cs"),
                       str(root / "ksp-plugin/src/KspMcpReflectionCache.cs"),
                       str(root / "tests/csharp/ReflectionCacheTests.cs"),
                       str(root / "tests/csharp/EventBufferTests.cs")]
            built = subprocess.run(command, capture_output=True, timeout=30)
            self.assertEqual(built.returncode, 0, built.stdout + built.stderr)
            ran = subprocess.run([str(exe)], capture_output=True, timeout=30)
            self.assertEqual(ran.returncode, 0, ran.stdout + ran.stderr)
            self.assertIn(b"240000", ran.stdout)
