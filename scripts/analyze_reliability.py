#!/usr/bin/env python3
"""Verify real client CSV, recompute aggregates and compare them with the client run log.

This script never generates or edits samples.
"""
import argparse
import csv
import json
import math
import re
from collections import defaultdict
from pathlib import Path
from statistics import mean


def analyze(csv_path, log_path):
    with csv_path.open(encoding="utf-8") as stream:
        rows = list(csv.DictReader(stream))
    series_names = ["baseline", "loss_5", "loss_10", "loss_20", "delay_100_loss_5", "jitter_loss_10"]
    assert len(rows) >= 300, "Need at least 300 actual commands"
    grouped = defaultdict(list)
    sequences = set()
    for row in rows:
        seq = int(row["sequence_number"])
        assert seq not in sequences, "Original commands must have unique sequence IDs"
        sequences.add(seq)
        grouped[row["series"]].append(row)
        for key, value in row.items():
            if key != "series" and value:
                assert math.isfinite(float(value)), f"Non-finite {key}"
        attempts = int(row["attempts"])
        delivered, failed = int(row["delivered"]), int(row["failed"])
        assert 1 <= attempts <= 5
        assert delivered in (0, 1) and failed in (0, 1) and delivered + failed == 1
        assert int(row["retransmissions"]) == attempts - 1
        assert int(row["ack_sample_used_for_rto"]) == int(delivered == 1 and attempts == 1)
        assert 100 <= float(row["final_rto_ms"]) <= 3000
        assert bool(row["time_to_ack_ms"]) == bool(delivered)
        assert bool(row["ack_received_at_ms"]) == bool(delivered)
        if delivered:
            elapsed = float(row["ack_received_at_ms"]) - float(row["first_sent_at_ms"])
            assert elapsed >= 0
            assert abs(elapsed - float(row["time_to_ack_ms"])) < 0.00001
        else:
            assert attempts == 5
    assert set(grouped) == set(series_names)
    summary = {}
    for name in series_names:
        group = grouped[name]
        assert len(group) >= 50
        assert [int(row["command_index"]) for row in group] == list(range(1, len(group) + 1))
        # Independently reconstruct every RTO update: probe RTT, then first-attempt ACK only.
        srtt, variance, rto = None, None, 1000.0
        for row in group:
            samples = [(row["probe_rtt_ms"], "rto_before_send_ms")]
            samples.append((row["time_to_ack_ms"] if int(row["ack_sample_used_for_rto"]) else "", "final_rto_ms"))
            for value, expected_key in samples:
                if value:
                    sample = float(value)
                    if srtt is None:
                        srtt, variance = sample, sample / 2
                    else:
                        variance = 0.75 * variance + 0.25 * abs(srtt - sample)
                        srtt = 0.875 * srtt + 0.125 * sample
                    rto = min(3000, max(100, srtt + 4 * variance))
                assert abs(rto - float(row[expected_key])) < 0.00002, f"RTO mismatch: {name}/{row['command_index']}"
        delivered_rows = [row for row in group if int(row["delivered"])]
        delivered = len(delivered_rows)
        failed = len(group) - delivered
        first = sum(int(row["attempts"]) == 1 for row in delivered_rows)
        item = {
            "sent": len(group), "delivered": delivered, "first_attempt": first,
            "first_attempt_percent": first * 100 / len(group),
            "retransmit_total": sum(int(row["retransmissions"]) for row in group),
            "failed": failed, "failed_percent": failed * 100 / len(group),
            "avg_attempts": mean(int(row["attempts"]) for row in group),
            "avg_time_to_ack_ms": mean(float(row["time_to_ack_ms"]) for row in delivered_rows) if delivered_rows else None,
            "final_rto_ms": float(group[-1]["final_rto_ms"]),
            "max_attempts": max(int(row["attempts"]) for row in group),
            "rto_min_ms": min(float(row["final_rto_ms"]) for row in group),
            "rto_max_ms": max(float(row["final_rto_ms"]) for row in group),
            "ack_samples_used": sum(int(row["ack_sample_used_for_rto"]) for row in group),
            "probe_samples_used": sum(bool(row["probe_rtt_ms"]) for row in group),
            "seed": int(group[0]["seed"]), "receive_seed": int(group[0]["receive_seed"]),
            "dropped_send_total": int(group[-1]["dropped_send_total"]),
            "dropped_receive_total": int(group[-1]["dropped_receive_total"]),
        }
        summary[name] = item
    logged = {}
    for line in log_path.read_text(encoding="utf-8").splitlines():
        match = re.match(r"RESULT ([^:]+): (.*)", line)
        if match:
            logged[match[1]] = dict(re.findall(r"(\w+)=([0-9.]+)", match[2]))
    assert set(logged) == set(summary), "Need console aggregates from all series"
    for name, item in summary.items():
        for key, value in logged[name].items():
            assert key in item and abs(float(value) - item[key]) <= 0.000002, f"Aggregate mismatch: {name}/{key}"
    return summary


if __name__ == "__main__":
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--csv", type=Path, default=root / "docs/reliability_samples.csv")
    parser.add_argument("--log", type=Path, default=root / "docs/reliability_run.log")
    parser.add_argument("--output", type=Path, default=root / "docs/reliability_summary.json")
    args = parser.parse_args()
    summary = analyze(args.csv, args.log)
    args.output.write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    print("CSV_INVARIANTS, RTO_RECONSTRUCTION, CONSOLE_AGGREGATES: PASS")
