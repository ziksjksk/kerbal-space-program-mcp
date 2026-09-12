# MCP efficiency benchmark

Run from the repository root:

```powershell
python benchmarks/run.py --output work/benchmark.json
python benchmarks/run.py --root C:\path\to\baseline-checkout --output work/baseline.json
```

This uses standard Python libraries, synthetic craft/telemetry and a loopback HTTP fixture. It never connects to the configured game bridge, runs a game action, or modifies an installed game. `--root` imports the selected checkout and launches its actual `python -m server` entrypoint for the stdio measurement. Use a source-integrity-fixed baseline (PR #3 / `e933351`), since the original main branch's Python module cannot import.

Measured on Windows / Python 3.12.10 on 2026-09-12:

| Measurement | Baseline | Optimized |
|---|---:|---:|
| 100-part chain validation, median of 5 | 0.7404 ms | 0.5308 ms |
| 1,000-part chain validation, median of 5 | 31.1140 ms | 5.3548 ms |
| 5,000-part chain validation, median of 5 | 756.6866 ms | 28.2997 ms |
| MCP result serialization, median of 20 | 6.8378 ms | 1.8806 ms |
| Same result on the wire, UTF-8 bytes | 447,112 | 293,756 |
| Control HTTP request during 250 ms telemetry wait, median of 5 | 251.1625 ms | 0.3981 ms |
| Actual stdio process ping during event wait, median of 3 | 552.1588 ms | 0.0866 ms |

The 20-snapshot result includes 64 synthetic build events per snapshot. Both the text and structured JSON contain the same data before and after; only text whitespace changed. This is a byte/serialization measurement, not a model-token estimate. Timing results depend on machine load; the sub-millisecond figures should be read as removal of head-of-line blocking rather than a promised production latency.

Validation is about 26.7x faster for this 5,000-part **chain**, the response is 34.3% smaller, and serialization is about 3.6x faster. Other craft shapes and result sizes differ. The benchmark deliberately creates concurrent requests; a client that only sends its next request after the previous response will not gain concurrent responsiveness automatically.

Separate regression tests cover a 6,000-part reverse-ordered tree, detached cycles, FIFO mutation dispatch, cancellation, queue overload, actual TCP connection reuse, UTF-8, and a real MCP subprocess. The standalone C# harness checks 240,000 event-ring/reference comparisons and reflection metadata behavior. On a machine with KSP assemblies and a freshly built DLL, an optional test exercises the compiled bridge's HTTP listener, command completion, section projection, post-wait cache freshness and queued-command expiration without Unity startup.

These tests do **not** measure KSP FPS, Unity garbage collection, rocket build completion time, or flight stability. Unity command serialization remains on the main thread because some command results reference mutable game/job dictionaries. Frame budgets apply between operations; one slow native command, batch, serialization or part creation can still overrun them.

The checked-in plugin DLL is rebuilt from these sources so this branch's installer uses the matching implementation. SHA-256: `9FEB4E2F80392B08189707A0DF57C9E95A845FF0B0EFABA275FFF4DE18C4C939`. No installed game files or published release archives were changed.
