"""Read-only operator commands over the Notify API."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from collections import Counter
from datetime import date, datetime, time, timezone
from importlib.metadata import version
from typing import Any

import httpx

from .client import NotifyClient, NotifyError


def utc_date(value: str) -> int:
    try:
        day = date.fromisoformat(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError("expected a UTC date in YYYY-MM-DD format") from error
    return int(datetime.combine(day, time.min, timezone.utc).timestamp() * 1000)


def add_filters(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--since", type=utc_date, help="UTC date, inclusive")
    parser.add_argument("--until", type=utc_date, help="UTC date, exclusive")
    parser.add_argument("--status")
    parser.add_argument("--id", help="Call ID or prefix")


def filtered(rows: list[dict[str, Any]], args: argparse.Namespace) -> list[dict[str, Any]]:
    return [row for row in rows if
            (args.since is None or row["startedAt"] >= args.since)
            and (args.until is None or row["startedAt"] < args.until)
            and (args.status is None or args.status in (row["status"], row.get("reason")))
            and (args.id is None or row["id"].startswith(args.id))]


def probes(client: NotifyClient, doctor: bool) -> dict[str, Any]:
    routes = {"live": "/health/live", "ready": "/health/ready"}
    if doctor:
        routes |= {"agent": "/api/v1/agents/self", "history": "/api/v1/calls", "asteriskRoute": "/api/v1/telephony/assistant"}
    result = {}
    for name, route in routes.items():
        try:
            response = client.http.get(route)
            result[name] = {"available": response.is_success, "httpStatus": response.status_code}
            if name == "agent" and response.is_success:
                result[name]["online"] = response.json().get("online")
        except httpx.HTTPError:
            result[name] = {"available": False, "httpStatus": None}
    return result


def trace(client: NotifyClient, call_id: str) -> dict[str, Any]:
    try:
        return client.call_trace(call_id)
    except NotifyError as error:
        if error.status != 404 or error.code != "trace_not_recorded":
            raise
        return {"callId": call_id, "events": [], "missing": "trace_not_recorded"}


def print_result(value: Any, machine: bool) -> None:
    if machine:
        print(json.dumps(value, ensure_ascii=False, indent=2))
    elif isinstance(value, list):
        if not value:
            print("Нет данных о звонках.")
        for row in value:
            at = datetime.fromtimestamp(row["startedAt"] / 1000, timezone.utc).isoformat(timespec="seconds")
            print(f'{at}  {row["id"]}  {row.get("target", "—")}  {row.get("reason") or row["status"]}')
    elif "events" in value:
        print(value["callId"])
        if not value["events"]:
            print("Нет данных об этапах.")
        for event in value["events"]:
            offset = event["offsetMs"]
            duration = "нет данных" if event.get("durationMs") is None else f'{event["durationMs"]:.0f} мс'
            print(f'+{offset:.0f} мс  {event["source"]}/{event["stage"]}  {event["status"]}  {duration}  {event.get("code") or ""}')
        if value.get("truncated"):
            print("Достигнут предел записи событий.")
    else:
        for key, entry in value.items():
            print(f"{key}\t{json.dumps(entry, ensure_ascii=False) if isinstance(entry, (dict, list)) else entry if entry is not None else 'Нет данных'}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="notifyctl", description="Read-only Notify diagnostics")
    parser.add_argument("--version", action="version", version=f"notifyctl {version('alxprgs-notify')}")
    parser.add_argument("--json", action="store_true", help="Print machine-readable JSON")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("status")
    sub.add_parser("doctor")
    calls = sub.add_parser("calls").add_subparsers(dest="operation", required=True)
    for command in ("list", "stats"):
        add_filters(calls.add_parser(command))
    for command in ("show", "trace"):
        calls.add_parser(command).add_argument("call_id")
    export = sub.add_parser("diagnostics").add_subparsers(dest="operation", required=True).add_parser("export")
    export.add_argument("call_id")
    args = parser.parse_args(argv)
    url = os.environ.get("NOTIFY_URL")
    token = os.environ.get("NOTIFY_TOKEN", "")
    if not url or (args.command != "status" and not token):
        parser.error("Set NOTIFY_URL and, for private requests, NOTIFY_TOKEN in the environment.")
    try:
        with NotifyClient(url, token, timeout=10) as client:
            if args.command in ("status", "doctor"):
                result = probes(client, args.command == "doctor")
            elif args.command == "diagnostics":
                row = client.call_status(args.call_id)
                keys = ("id", "sourceId", "agentId", "sessionId", "target", "status", "reason", "startedAt", "durationMs")
                result = {"call": {key: row.get(key) for key in keys}, "trace": trace(client, args.call_id)}
            elif args.operation == "trace":
                result = trace(client, args.call_id)
            elif args.operation == "show":
                result = client.call_status(args.call_id)
            else:
                rows = filtered(client.call_history(), args)
                result = rows if args.operation == "list" else {
                    "count": len(rows), "result": dict(Counter(row.get("reason") or row["status"] for row in rows)),
                    "scope": "до 100 последних записей источника",
                }
        print_result(result, args.json or args.command == "diagnostics")
        return 0
    except (NotifyError, httpx.HTTPError, ValueError, KeyError, TypeError) as error:
        code = error.code if isinstance(error, NotifyError) and isinstance(error.code, str) and re.fullmatch(r"[a-z0-9_]{1,64}", error.code) else None
        safe = {"error": code or type(error).__name__}
        print(json.dumps(safe, ensure_ascii=False) if args.json else f"Данные Notify недоступны ({safe['error']}).", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
