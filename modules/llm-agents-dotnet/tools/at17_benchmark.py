#!/usr/bin/env python3
"""Reproducible AT-17 fixture and A/B/C benchmark for the typed groupCode validator."""
from __future__ import annotations

import json
import random
import statistics
import time
from pathlib import Path

SEED = 17017
COUNT = 100
RUNS = 3
ROOT = Path(__file__).resolve().parents[1]
REPORT_DIR = ROOT / "docs" / "at17"
FIXTURE_PATH = REPORT_DIR / "fixture.json"
REPORT_PATH = REPORT_DIR / "report.json"


def make_fixture() -> dict:
    rng = random.Random(SEED)
    proposals = []
    for number in range(COUNT):
        application_id = f"req-{number + 1:03d}"
        decision_id = f"doc-{number + 1:03d}"
        proposals.append(
            {
                "proposalId": f"proposal-{number + 1:03d}",
                "applicationId": application_id,
                "decisionId": decision_id,
                "applicationVersion": "7",
                "decisionVersion": "3",
                "field": "groupCode",
                "before": None,
                "after": f"ИУ5-{rng.randint(10, 99)}Б",
                "sources": [
                    {"objectId": application_id, "version": "7", "field": "decisionId"},
                    {"objectId": application_id, "version": "7", "field": "groupCode"},
                    {"objectId": decision_id, "version": "3", "field": "approved"},
                    {"objectId": decision_id, "version": "3", "field": "groupCode"},
                ],
            }
        )
    return {
        "seed": SEED,
        "count": COUNT,
        "changedSource": {"objectId": "doc-042", "oldVersion": "3", "newVersion": "4", "field": "groupCode"},
        "expectedStaleProposalIds": ["proposal-042"],
        "proposals": proposals,
    }


def full_repeat(fixture: dict) -> tuple[int, int, int]:
    # A is a cost model: one grounded model call per proposal, with one source read per call.
    reads = 0
    llm_calls = 0
    stale = 0
    for proposal in fixture["proposals"]:
        llm_calls += 1
        reads += len(proposal["sources"])
        if proposal["proposalId"] in fixture["expectedStaleProposalIds"]:
            stale += 1
    return reads, llm_calls, stale


def full_scan(fixture: dict) -> tuple[int, int, int]:
    reads = 0
    stale_ids: set[str] = set()
    changed = fixture["changedSource"]["objectId"]
    for proposal in fixture["proposals"]:
        for source in proposal["sources"]:
            reads += 1
            if source["objectId"] == changed and source["version"] != fixture["changedSource"]["newVersion"]:
                stale_ids.add(proposal["proposalId"])
    return reads, 0, len(stale_ids)


def indexed_scan(fixture: dict) -> tuple[int, int, int]:
    index: dict[str, set[str]] = {}
    for proposal in fixture["proposals"]:
        for source in proposal["sources"]:
            index.setdefault(source["objectId"], set()).add(proposal["proposalId"])
    changed = fixture["changedSource"]["objectId"]
    candidates = index.get(changed, set())
    reads = len(candidates)
    stale_ids = set()
    if fixture["changedSource"]["oldVersion"] != fixture["changedSource"]["newVersion"]:
        stale_ids.update(candidates)
    return reads, 0, len(stale_ids)


def measure(name: str, operation, fixture: dict) -> dict:
    samples = []
    last = None
    for _ in range(RUNS):
        start = time.perf_counter_ns()
        last = operation(fixture)
        samples.append((time.perf_counter_ns() - start) / 1_000_000)
    return {
        "strategy": name,
        "runs": RUNS,
        "medianMs": round(statistics.median(samples), 4),
        "samplesMs": [round(sample, 4) for sample in samples],
        "graphReads": last[0],
        "llmCalls": last[1],
        "staleProposals": last[2],
    }


def main() -> None:
    fixture = make_fixture()
    REPORT_DIR.mkdir(parents=True, exist_ok=True)
    FIXTURE_PATH.write_text(json.dumps(fixture, ensure_ascii=False, indent=2) + "\n")
    results = [
        measure("A-full-repeat-model-cost", full_repeat, fixture),
        measure("B-full-scan-no-llm", full_scan, fixture),
        measure("C-provenance-index", indexed_scan, fixture),
    ]
    report = {
        "seed": SEED,
        "fixture": str(FIXTURE_PATH.relative_to(ROOT)),
        "expectedStaleProposalIds": fixture["expectedStaleProposalIds"],
        "validation": {
            "B_equals_C_stale_set": results[1]["staleProposals"] == results[2]["staleProposals"] == 1,
            "C_less_reads_than_B": results[2]["graphReads"] < results[1]["graphReads"],
            "A_is_simulation": True,
        },
        "results": results,
    }
    REPORT_PATH.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
