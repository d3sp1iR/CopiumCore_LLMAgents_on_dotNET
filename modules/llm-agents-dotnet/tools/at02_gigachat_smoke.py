#!/usr/bin/env python3
"""Credential-gated GigaChat AT-02 runner.

The live path is intentionally delegated to the API/runtime so the C# AgentExecutor
performs the getObject -> tool result -> structured response cycle. Without credentials
this emits machine-readable skipped evidence and leaves Ollama unaffected.
"""
import argparse
import json
import os
import sys
import urllib.request


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--api", default="http://localhost:5000")
    parser.add_argument("--input", default="Read req-17 and propose its approved groupCode")
    args = parser.parse_args()
    credentials = os.getenv("GIGACHAT_CREDENTIALS")
    if not credentials:
        print(json.dumps({"status": "skipped", "reason": "GIGACHAT_CREDENTIALS is not configured", "provider": "GigaChat"}))
        return 0

    request = urllib.request.Request(
        args.api.rstrip("/") + "/api/llm/v1/runs",
        data=json.dumps({"agentId": "at02-gigachat", "sessionId": "at02", "taskId": "at02", "input": args.input}).encode(),
        headers={"Content-Type": "application/json", "Idempotency-Key": "at02-gigachat"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request) as response:
            payload = json.load(response)
        print(json.dumps({"status": "submitted", "provider": "GigaChat", "run": payload}))
        return 0
    except Exception as exc:
        print(json.dumps({"status": "failed", "provider": "GigaChat", "reason": str(exc)}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
