"""Compare two frozen ReaderBenchmark builds on the same private journal corpus."""
import argparse
import json
import os
from pathlib import Path
import statistics
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("before", type=Path, help="Directory containing the baseline ReaderBenchmark.dll")
parser.add_argument("after", type=Path, help="Directory containing the candidate ReaderBenchmark.dll")
parser.add_argument("corpus", type=Path)
parser.add_argument("output", type=Path)
parser.add_argument("--runs", type=int, default=7)
parser.add_argument("--modes", nargs="+", default=["current", "context"])
args = parser.parse_args()
if args.runs < 1:
    parser.error("--runs must be positive")
env = dict(os.environ, DOTNET_TieredCompilation="0", DOTNET_gcServer="0")
samples = []
for repeat in range(args.runs):
    modes = args.modes[repeat % len(args.modes):] + args.modes[:repeat % len(args.modes)]
    if repeat % 2:
        modes.reverse()
    for mode_index, mode in enumerate(modes):
        variants = [("before", args.before), ("after", args.after)]
        if (repeat + mode_index) % 2:
            variants.reverse()
        pair = []
        for variant, folder in variants:
            row = json.loads(subprocess.check_output([
                "dotnet", str(folder / "ReaderBenchmark.dll"), str(args.corpus), mode, "1", "1"
            ], env=env, text=True, encoding="utf-8"))
            row.update(variant=variant, repeat=repeat)
            samples.append(row)
            pair.append(row)
            print(json.dumps({"repeat": repeat, "variant": variant, "mode": mode,
                              "seconds": round(row["seconds"], 4)}), flush=True)
        for field in ("records", "Checksum", "DataCharacters", "inputBytes", "bases"):
            if pair[0][field] != pair[1][field]:
                raise RuntimeError(f"Different {field} for {mode}")
        args.output.write_text(json.dumps({"samples": samples}, indent=2), encoding="utf-8")

summary = []
for mode in args.modes:
    result = {"mode": mode}
    for variant in ("before", "after"):
        rows = [r for r in samples if r["mode"] == mode and r["variant"] == variant]
        result[variant] = {
            "runs": len(rows), "medianSeconds": statistics.median(r["seconds"] for r in rows),
            "minSeconds": min(r["seconds"] for r in rows), "maxSeconds": max(r["seconds"] for r in rows),
            "medianEventsPerSecond": statistics.median(r["eventsPerSecond"] for r in rows),
            "medianAllocatedBytesPerEvent": statistics.median(r["allocatedBytesPerEvent"] for r in rows),
            "medianPeakWorkingSetMiB": statistics.median(r["peakWorkingSetBytes"] / 1048576 for r in rows)
        }
    result["timeReductionPercent"] = 100 * (1 - result["after"]["medianSeconds"] / result["before"]["medianSeconds"])
    summary.append(result)
args.output.write_text(json.dumps({"summary": summary, "samples": samples}, indent=2), encoding="utf-8")
print(json.dumps(summary), flush=True)
