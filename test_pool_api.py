#!/usr/bin/env python3
"""
Miningcore API Integration Test

Usage:
    python3 test_pool_api.py
    python3 test_pool_api.py --url http://localhost:4000 --pool mypool
    python3 test_pool_api.py --url http://localhost:4000 --pool mypool --address <addr>
    python3 test_pool_api.py --verbose
    python3 test_pool_api.py --no-ws

Flags:
    --url       Pool API base URL   (default: http://localhost:4000)
    --pool      Pool ID             (required; prompted if missing)
    --address   Miner address       (optional; prompted; skips miner tests if blank)
    --timeout   Request timeout s   (default: 10)
    --verbose   Print full JSON
    --no-ws     Skip WebSocket test

Dependencies:
    pip install websockets
"""

import argparse
import asyncio
import json
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any
from urllib.parse import quote, urlparse

try:
    import websockets
    HAS_WS = True
except ImportError:
    HAS_WS = False

RESET  = "\033[0m"
BOLD   = "\033[1m"
GREEN  = "\033[32m"
RED    = "\033[31m"
YELLOW = "\033[33m"
CYAN   = "\033[36m"
DIM    = "\033[2m"

def _c(color: str, text: str) -> str: return f"{color}{text}{RESET}"
def ok(msg: str)   -> str: return _c(GREEN,  f"  OK   {msg}")
def fail(msg: str) -> str: return _c(RED,    f"  FAIL {msg}")
def warn(msg: str) -> str: return _c(YELLOW, f"  WARN {msg}")
def info(msg: str) -> str: return _c(CYAN,   f"  ....  {msg}")
def head(msg: str) -> str: return f"\n{BOLD}{CYAN}{msg}{RESET}"


@dataclass
class Result:
    name: str
    url: str
    passed: bool
    status: int | None = None
    error: str | None = None
    warnings: list[str] = field(default_factory=list)
    ms: float = 0.0
    data: Any = None


def fetch(url: str, timeout: int, method: str = "GET",
          body: dict | None = None) -> tuple[int, Any]:
    raw = json.dumps(body).encode() if body else None
    headers = {"Content-Type": "application/json", "Accept": "application/json"}
    req = urllib.request.Request(url, data=raw, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        text = resp.read().decode("utf-8")
        if not text.strip():
            return resp.status, None
        try:
            return resp.status, json.loads(text)
        except json.JSONDecodeError:
            return resp.status, text  # plain-text body


def _preview(data: Any) -> str:
    """Compact one-line response summary for discovery."""
    if data is None:
        return "null"
    if isinstance(data, str):
        return repr(data[:80])
    if isinstance(data, list):
        if not data:
            return "[]"
        sample = data[0]
        keys = list(sample.keys()) if isinstance(sample, dict) else type(sample).__name__
        return f"[{len(data)} items]  keys={keys}"
    if isinstance(data, dict):
        if len(data) == 1:
            k, v = next(iter(data.items()))
            if isinstance(v, list):
                sample = v[0] if v else {}
                inner = list(sample.keys()) if isinstance(sample, dict) else "?"
                return f"{{{k}: [{len(v)} items]  keys={inner}}}"
            if isinstance(v, dict):
                return f"{{{k}: keys={list(v.keys())}}}"
        return f"keys={list(data.keys())}"
    return repr(data)[:120]


class Tester:
    def __init__(self, base: str, pool: str, address: str | None,
                 timeout: int, verbose: bool, skip_ws: bool):
        self.base    = base.rstrip("/")
        self.pool    = pool
        self.address = address
        self.timeout = timeout
        self.verbose = verbose
        self.skip_ws = skip_ws
        self.results: list[Result] = []

    def _run(self, name: str, url: str, *,
             method: str = "GET",
             body: dict | None = None,
             expect: int = 200,
             also_ok: tuple[int, ...] = (),
             req_fields: list[str] | None = None,
             non_neg: list[str] | None = None,
             warn_empty: bool = False) -> Result:

        r = Result(name=name, url=url, passed=False)
        t0 = time.monotonic()
        try:
            status, data = fetch(url, self.timeout, method=method, body=body)
            r.status = status
            r.data   = data
            r.ms     = (time.monotonic() - t0) * 1000

            if status != expect:
                if status in also_ok:
                    r.passed = True
                    _msg = {403: "IP mismatch or auth required", 404: "not found (no data yet)"}.get(status, "acceptable non-200")
                    r.warnings.append(f"HTTP {status} ({_msg})")
                    self._print(r)
                    self.results.append(r)
                    return r
                r.error = f"HTTP {status} (expected {expect})"
                self._print(r); self.results.append(r); return r

            if req_fields:
                target: dict | None = None
                if isinstance(data, dict) and "pools" in data:
                    lst = data["pools"]
                    target = lst[0] if lst else {}
                elif isinstance(data, dict) and "pool" in data:
                    target = data["pool"]
                elif isinstance(data, list) and data and isinstance(data[0], dict):
                    target = data[0]
                elif isinstance(data, dict):
                    target = data
                if target is not None:
                    for f in req_fields:
                        if f not in target:
                            r.warnings.append(f"field '{f}' missing")

            if non_neg:
                target = data
                if isinstance(data, dict) and "pools" in data:
                    lst = data.get("pools", [])
                    target = lst[0] if lst else {}
                elif isinstance(data, dict) and "pool" in data:
                    target = data["pool"]
                if isinstance(target, dict):
                    for f in non_neg:
                        v = target.get(f)
                        if v is not None and isinstance(v, (int, float)) and v < 0:
                            r.warnings.append(f"'{f}' is negative: {v}")

            if warn_empty and isinstance(data, list) and not data:
                r.warnings.append("empty list (no data yet)")

            r.passed = True

        except urllib.error.HTTPError as e:
            r.status = e.code
            r.ms     = (time.monotonic() - t0) * 1000
            if e.code in also_ok:
                r.passed = True
                _msg = {403: "IP mismatch or auth required", 404: "not found (no data yet)"}.get(e.code, "acceptable non-200")
                r.warnings.append(f"HTTP {e.code} ({_msg})")
            else:
                snippet = e.read().decode("utf-8", errors="replace")[:200]
                r.error = f"HTTP {e.code}: {snippet}"
        except urllib.error.URLError as e:
            r.ms    = (time.monotonic() - t0) * 1000
            r.error = f"connection failed: {e.reason}"
        except Exception as e:
            r.ms    = (time.monotonic() - t0) * 1000
            r.error = str(e)

        self._print(r)
        self.results.append(r)
        return r

    def _print(self, r: Result):
        ms_s   = f"{r.ms:.0f}ms"
        st_s   = f"[{r.status}]" if r.status else "[---]"
        suffix = _c(DIM, f"{st_s} {ms_s}")

        if r.passed and not r.warnings:
            print(ok(f"{r.name}  {suffix}"))
        elif r.passed:
            print(warn(f"{r.name}  {suffix}"))
            for w in r.warnings:
                print(f"       {_c(YELLOW, f'^ {w}')}")
        else:
            print(fail(f"{r.name}  {suffix}"))
            if r.error:
                print(f"       {_c(RED, f'^ {r.error}')}")

        if r.data is not None and not r.warnings:
            if self.verbose:
                lines = json.dumps(r.data, ensure_ascii=False, indent=2,
                                   default=str).splitlines()
                cap = lines
                print(_c(DIM, "\n".join(cap)))
            else:
                print(f"       {_c(DIM, _preview(r.data))}")

    async def _ws_test(self) -> Result:
        parsed = urlparse(self.base)
        ws_proto = "wss" if parsed.scheme == "https" else "ws"
        ws_port = parsed.port or (443 if parsed.scheme == "https" else 80)
        ws_url = f"{ws_proto}://{parsed.hostname}:{ws_port}/notifications?poolId={quote(self.pool, safe='')}"
        r = Result(name="WS /notifications", url=ws_url, passed=False)
        t0 = time.monotonic()
        try:
            async with websockets.connect(ws_url, open_timeout=self.timeout) as ws:
                raw  = await asyncio.wait_for(ws.recv(), timeout=self.timeout)
                msg  = json.loads(raw)
                r.ms = (time.monotonic() - t0) * 1000
                if msg.get("type") == "greeting":
                    r.passed = True
                    r.data   = msg
                else:
                    r.error = f"unexpected first message type: {msg.get('type')}"
        except Exception as e:
            r.ms    = (time.monotonic() - t0) * 1000
            r.error = str(e)
        self._print(r)
        self.results.append(r)
        return r

    def _info_fields(self, data: Any, fields: list[str]):
        target = data
        if isinstance(data, dict):
            target = data.get("pool", data)
        if isinstance(target, dict):
            vals = "  ".join(f"{f}={target.get(f)}" for f in fields)
            print(info(vals))

    def run_all(self):
        b = self.base
        p = self.pool
        a = self.base  # admin is same host

        print(head("PRE-FLIGHT"))

        health = self._run("GET /api/health-check", f"{b}/api/health-check")
        if health.status is None:
            print(_c(RED, f"\n  Server unreachable at {b}. Is the pool running?\n"))
            self._summary(); sys.exit(1)

        self._run("GET /api/help", f"{b}/api/help")

        print(head("PUBLIC — Pools"))

        # Fields added to PoolInfo top-level (commit 13ee189)
        pool_fields_toplevel = [
            "totalPaymentsCount", "blocks24h", "lastPaymentTime",
            "workersOnline", "workersOffline",
        ]
        non_neg = ["workersOnline", "workersOffline", "blocks24h", "totalPaymentsCount"]

        pools_r = self._run(
            "GET /api/pools", f"{b}/api/pools",
            req_fields=pool_fields_toplevel, non_neg=non_neg,
        )
        # paymentIntervalSeconds lives inside paymentProcessing
        if pools_r.passed and pools_r.data is not None:
            lst = (pools_r.data.get("pools", []) if isinstance(pools_r.data, dict)
                   else pools_r.data if isinstance(pools_r.data, list) else [])
            for pool_item in lst:
                pp = pool_item.get("paymentProcessing") or {}
                if "paymentIntervalSeconds" not in pp:
                    pools_r.warnings.append(
                        f"pool '{pool_item.get('id')}': "
                        "paymentProcessing.paymentIntervalSeconds missing"
                    )

        pool_known = False
        if pools_r.passed and pools_r.data is not None:
            lst = (pools_r.data.get("pools", []) if isinstance(pools_r.data, dict)
                   else pools_r.data if isinstance(pools_r.data, list) else [])
            ids = [x.get("id") for x in lst]
            print(info(f"pools found: {ids}"))
            if p in ids:
                pool_known = True
            else:
                print(warn(f"pool '{p}' not in /api/pools; available: {ids}"))

        if not pool_known:
            print(warn(f"Skipping pool-specific tests — pool '{p}' not loaded by server"))
        else:
            pool_r = self._run(
                f"GET /api/pools/{p}", f"{b}/api/pools/{p}",
                req_fields=pool_fields_toplevel,
                non_neg=["workersOnline", "workersOffline", "blocks24h"],
            )
            # paymentIntervalSeconds lives inside paymentProcessing
            if pool_r.passed and pool_r.data is not None:
                pool_obj = pool_r.data.get("pool") or {}
                pp = pool_obj.get("paymentProcessing") or {}
                if "paymentIntervalSeconds" not in pp:
                    pool_r.warnings.append(
                        "paymentProcessing.paymentIntervalSeconds missing"
                    )
            if pool_r.passed:
                self._info_fields(pool_r.data,
                    ["workersOnline", "workersOffline", "blocks24h",
                     "totalPaymentsCount", "lastPaymentTime"])

            self._run(f"GET /api/pools/{p}/performance", f"{b}/api/pools/{p}/performance")

            print(head("PUBLIC — Blocks"))
            self._run(f"GET /api/pools/{p}/blocks",
                      f"{b}/api/pools/{p}/blocks?page=0&pageSize=10", warn_empty=True)
            self._run(f"GET /api/v2/pools/{p}/blocks",
                      f"{b}/api/v2/pools/{p}/blocks?page=0&pageSize=10")
            self._run("GET /api/blocks (cluster)",
                      f"{b}/api/blocks?page=0&pageSize=10")

            print(head("PUBLIC — Payments"))
            self._run(f"GET /api/pools/{p}/payments",
                      f"{b}/api/pools/{p}/payments?page=0&pageSize=10", warn_empty=True)
            self._run(f"GET /api/v2/pools/{p}/payments",
                      f"{b}/api/v2/pools/{p}/payments?page=0&pageSize=10")

            print(head("PUBLIC — Miners"))
            self._run(f"GET /api/pools/{p}/miners",
                                 f"{b}/api/pools/{p}/miners?page=0&pageSize=10",
                                 warn_empty=True)

            address = self.address
            if address:
                ms = address[:20] + "..."
                print(head("PUBLIC — Miner Detail"))

                self._run(f"GET miners/{ms}",
                          f"{b}/api/pools/{p}/miners/{address}",
                          req_fields=["workersOnline", "workersOffline"],
                          non_neg=["workersOnline", "workersOffline"])
                self._run(f"GET miners/{ms}/blocks",
                          f"{b}/api/pools/{p}/miners/{address}/blocks?page=0&pageSize=5",
                          warn_empty=True)
                self._run(f"GET miners/{ms}/payments",
                          f"{b}/api/pools/{p}/miners/{address}/payments?page=0&pageSize=5",
                          warn_empty=True)
                self._run(f"GET miners/{ms}/balancechanges",
                          f"{b}/api/pools/{p}/miners/{address}/balancechanges?page=0&pageSize=5")
                self._run(f"GET miners/{ms}/earnings/daily",
                          f"{b}/api/pools/{p}/miners/{address}/earnings/daily?page=0&pageSize=7")
                self._run(f"GET miners/{ms}/performance",
                          f"{b}/api/pools/{p}/miners/{address}/performance")
                self._run(f"GET miners/{ms}/settings",
                          f"{b}/api/pools/{p}/miners/{address}/settings",
                          also_ok=(404,))
                # POST /settings requires that the request IP matches a recent mining IP.
                # 200 = success, 400 = bad request body, 403 = IP mismatch, 404 = address unknown.
                # All four outcomes mean the endpoint exists and is reachable.
                self._run(f"POST miners/{ms}/settings",
                          f"{b}/api/pools/{p}/miners/{address}/settings",
                          method="POST",
                          body={"ipAddress": "127.0.0.1",
                                "settings": {"paymentThreshold": 0.01}},
                          also_ok=(400, 403, 404))

                print(head("PUBLIC — Miner Detail (v2)"))
                self._run(f"GET v2 miners/{ms}/blocks",
                          f"{b}/api/v2/pools/{p}/miners/{address}/blocks?page=0&pageSize=5")
                self._run(f"GET v2 miners/{ms}/payments",
                          f"{b}/api/v2/pools/{p}/miners/{address}/payments?page=0&pageSize=5")
                self._run(f"GET v2 miners/{ms}/balancechanges",
                          f"{b}/api/v2/pools/{p}/miners/{address}/balancechanges?page=0&pageSize=5")
                self._run(f"GET v2 miners/{ms}/earnings/daily",
                          f"{b}/api/v2/pools/{p}/miners/{address}/earnings/daily?page=0&pageSize=7")
            else:
                print(warn("No address given — skipping miner detail tests"))

        print(head("ADMIN (private, localhost-only)"))
        print(info(f"admin URL: {a}"))

        self._run("GET /api/admin/stats/gc",          f"{a}/api/admin/stats/gc")
        self._run("POST /api/admin/forcegc",          f"{a}/api/admin/forcegc", method="POST", body={})

        # Global payment toggle — restore to enabled
        self._run("GET /api/admin/payment/enable",    f"{a}/api/admin/payment/processing/enable")
        self._run("GET /api/admin/payment/disable",   f"{a}/api/admin/payment/processing/disable")
        self._run("GET /api/admin/payment/enable (restore)", f"{a}/api/admin/payment/processing/enable")

        # Per-pool payment toggle — restore to enabled
        self._run(f"GET /api/admin/payment/{p}/enable",
                  f"{a}/api/admin/payment/processing/{p}/enable")
        self._run(f"GET /api/admin/payment/{p}/disable",
                  f"{a}/api/admin/payment/processing/{p}/disable")
        self._run(f"GET /api/admin/payment/{p}/enable (restore)",
                  f"{a}/api/admin/payment/processing/{p}/enable")

        # Pool enable/disable toggle — restore to enabled
        self._run(f"GET /api/admin/{p}/enable",       f"{a}/api/admin/{p}/enable")
        self._run(f"GET /api/admin/{p}/disable",      f"{a}/api/admin/{p}/disable")
        self._run(f"GET /api/admin/{p}/enable (restore)", f"{a}/api/admin/{p}/enable")

        self._run("GET /api/admin/logging/level/info",
                  f"{a}/api/admin/logging/level/info")

        if self.address:
            ms = self.address[:20] + "..."
            self._run(f"GET admin miners/{ms}/getbalance",
                      f"{a}/api/admin/pools/{p}/miners/{self.address}/getbalance")
            self._run(f"GET admin miners/{ms}/settings",
                      f"{a}/api/admin/pools/{p}/miners/{self.address}/settings",
                      also_ok=(404,))
            # Admin POST settings: no IP verification required, just valid body.
            # 200 = saved, 404 = pool/address unknown (treat as warning).
            self._run(f"POST admin miners/{ms}/settings",
                      f"{a}/api/admin/pools/{p}/miners/{self.address}/settings",
                      method="POST",
                      body={"paymentThreshold": 0.01},
                      also_ok=(404,))

        if not self.skip_ws:
            print(head("WebSocket"))
            if HAS_WS:
                asyncio.run(self._ws_test())
            else:
                print(warn("websockets not installed — skip. Run: pip install websockets"))

        self._summary()

    def _summary(self):
        total  = len(self.results)
        passed = sum(1 for r in self.results if r.passed)
        failed = sum(1 for r in self.results if not r.passed)
        warned = sum(1 for r in self.results if r.passed and r.warnings)
        avg    = (sum(r.ms for r in self.results) / total) if total else 0

        print(f"\n{'='*55}")
        print(f"{BOLD}  SUMMARY  {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}{RESET}")
        print(f"{'='*55}")
        print(f"  Total   : {total}")
        print(f"  {_c(GREEN,  f'Passed  : {passed}')}")
        if warned: print(f"  {_c(YELLOW, f'Warnings: {warned}')}")
        if failed: print(f"  {_c(RED,    f'Failed  : {failed}')}")
        print(f"  Avg     : {avg:.0f}ms")
        print(f"{'='*55}")

        if failed:
            print(f"\n{BOLD}  Failed tests:{RESET}")
            for r in self.results:
                if not r.passed:
                    print(f"    {_c(RED, 'x')} {r.name}")
                    print(f"        {_c(DIM, r.url)}")
                    if r.error:
                        print(f"        {_c(RED, r.error)}")

        if warned:
            print(f"\n{BOLD}  Warnings:{RESET}")
            for r in self.results:
                if r.passed and r.warnings:
                    print(f"    {_c(YELLOW, '!')} {r.name}")
                    for w in r.warnings:
                        print(f"        {_c(YELLOW, w)}")

        print()
        if failed == 0:
            print(_c(GREEN, "  All endpoints OK"))
        else:
            print(_c(RED, f"  {failed} endpoint(s) failed"))
        print()


def main():
    ap = argparse.ArgumentParser(
        description="Miningcore API Integration Test",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    ap.add_argument("--url",     default="", help="Pool base URL")
    ap.add_argument("--pool",    default="", help="Pool ID")
    ap.add_argument("--address", default="", help="Miner address (optional)")
    ap.add_argument("--timeout", default=10, type=int)
    ap.add_argument("--verbose", default=-1, type=int, choices=[0, 1],
                    help="Show full API responses: 1=yes 0=no")
    ap.add_argument("--no-ws",   action="store_true", dest="no_ws")
    args = ap.parse_args()

    print(f"\n{BOLD}{CYAN}Miningcore API Integration Test{RESET}\n")

    base = args.url or input("  Base URL [http://localhost:4000]: ").strip() or "http://localhost:4000"

    pool = args.pool
    while not pool:
        pool = input("  Pool ID: ").strip()
        if not pool:
            print("  Pool ID is required.")

    address = args.address or input("  Miner address (blank to skip miner tests): ").strip() or None

    if args.verbose == -1:
        v_input = input("  Verbose responses? [0/1, default 0]: ").strip()
        verbose = v_input == "1"
    else:
        verbose = bool(args.verbose)

    addr_display = (address[:20] + "...") if address else "(none)"
    print(f"  {_c(DIM, f'base={base}  pool={pool}  address={addr_display}  timeout={args.timeout}s  verbose={int(verbose)}')}\n")

    Tester(
        base=base, pool=pool,
        address=address,
        timeout=args.timeout,
        verbose=verbose,
        skip_ws=args.no_ws,
    ).run_all()


if __name__ == "__main__":
    main()
