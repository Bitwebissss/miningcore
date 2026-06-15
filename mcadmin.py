#!/usr/bin/env python3
"""Miningcore local admin CLI — talks directly to 127.0.0.1:4000 (bypasses nginx)"""

import json
import sys
import urllib.request
import urllib.error

BASE = "http://127.0.0.1:4001/api/admin"

# ── HTTP helpers ───────────────────────────────────────────────────────────────

def req(method, path, body=None):
    url = BASE + path
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"} if data else {}
    r = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(r, timeout=10) as res:
            raw = res.read().decode()
            try:
                return json.loads(raw)
            except json.JSONDecodeError:
                return raw.strip()
    except urllib.error.HTTPError as e:
        return f"HTTP {e.code}: {e.read().decode().strip()}"
    except urllib.error.URLError as e:
        return f"Connection error: {e.reason}"
    except Exception as e:
        return f"Error: {e}"

def get(path):              return req("GET",  path)
def post(path, body=None):  return req("POST", path, body)

def p(v):
    if isinstance(v, (dict, list)):
        print(json.dumps(v, indent=2, ensure_ascii=False))
    else:
        print(v)

def ask(prompt, default=""):
    suffix = f" [{default}]" if default else ""
    v = input(f"  {prompt}{suffix}: ").strip()
    return v or default

def confirm(prompt):
    return input(f"  {prompt} [y/N]: ").strip().lower() == "y"

# ── menu ───────────────────────────────────────────────────────────────────────

MENU = """
╔══════════════════════════════════════════╗
║        Miningcore Admin CLI              ║
║        → 127.0.0.1:4001 (local)         ║
╠══════════════════════════════════════════╣
║  System                                  ║
║   1  GC stats / memory                  ║
║   2  Force garbage collection            ║
║   3  Set log level                       ║
╠══════════════════════════════════════════╣
║  Payments — all pools                    ║
║   4  Enable  payments (all pools)        ║
║   5  Disable payments (all pools)        ║
╠══════════════════════════════════════════╣
║  Payments — single pool                  ║
║   6  Enable  payments (pool)             ║
║   7  Disable payments (pool)             ║
╠══════════════════════════════════════════╣
║  Pool state                              ║
║   8  Enable  pool                        ║
║   9  Disable pool                        ║
╠══════════════════════════════════════════╣
║  Miners                                  ║
║  10  Get miner balance                   ║
║  11  Get miner settings                  ║
║  12  Set miner payment threshold         ║
╠══════════════════════════════════════════╣
║   0  Exit                                ║
╚══════════════════════════════════════════╝"""

# ── handlers ──────────────────────────────────────────────────────────────────

def gc_stats():
    p(get("/stats/gc"))

def force_gc():
    if confirm("Force GC now?"):
        p(post("/forcegc"))

def set_log_level():
    levels = ["Trace", "Debug", "Info", "Warn", "Error", "Fatal"]
    print(f"  Levels: {', '.join(levels)}")
    lvl = ask("Level", "Info")
    if lvl not in levels:
        print(f"  Invalid level. Choose from: {', '.join(levels)}")
        return
    p(get(f"/logging/level/{lvl}"))

def payments_all_enable():
    if confirm("Enable payments for ALL pools?"):
        p(get("/payment/processing/enable"))

def payments_all_disable():
    if confirm("Disable payments for ALL pools?"):
        p(get("/payment/processing/disable"))

def payments_pool_enable():
    pool = ask("Pool ID")
    if pool:
        p(get(f"/payment/processing/{pool}/enable"))

def payments_pool_disable():
    pool = ask("Pool ID")
    if pool:
        if confirm(f"Disable payments for pool '{pool}'?"):
            p(get(f"/payment/processing/{pool}/disable"))

def pool_enable():
    pool = ask("Pool ID")
    if pool:
        p(get(f"/{pool}/enable"))

def pool_disable():
    pool = ask("Pool ID")
    if pool:
        if confirm(f"Disable pool '{pool}'?"):
            p(get(f"/{pool}/disable"))

def miner_balance():
    pool = ask("Pool ID")
    addr = ask("Miner address")
    if pool and addr:
        p(get(f"/pools/{pool}/miners/{addr}/getbalance"))

def miner_settings_get():
    pool = ask("Pool ID")
    addr = ask("Miner address")
    if pool and addr:
        p(get(f"/pools/{pool}/miners/{addr}/settings"))

def miner_settings_set():
    pool = ask("Pool ID")
    addr = ask("Miner address")
    if not (pool and addr):
        return
    print("  Current settings:")
    cur = get(f"/pools/{pool}/miners/{addr}/settings")
    p(cur)
    print()
    threshold = ask("New payment threshold (leave blank to cancel)", "")
    if threshold:
        try:
            body = {"paymentThreshold": float(threshold)}
            p(post(f"/pools/{pool}/miners/{addr}/settings", body))
        except ValueError:
            print("  Invalid number.")

# ── dispatch ──────────────────────────────────────────────────────────────────

ACTIONS = {
    "1":  gc_stats,
    "2":  force_gc,
    "3":  set_log_level,
    "4":  payments_all_enable,
    "5":  payments_all_disable,
    "6":  payments_pool_enable,
    "7":  payments_pool_disable,
    "8":  pool_enable,
    "9":  pool_disable,
    "10": miner_balance,
    "11": miner_settings_get,
    "12": miner_settings_set,
}

def main():
    while True:
        print(MENU)
        try:
            ch = input("  > ").strip()
        except (KeyboardInterrupt, EOFError):
            print("\n  Bye.")
            sys.exit(0)

        if ch == "0":
            print("  Bye.")
            break

        action = ACTIONS.get(ch)
        if action:
            print()
            action()
        else:
            print("  Invalid choice.")

        try:
            input("\n  [Enter] to continue...")
        except (KeyboardInterrupt, EOFError):
            print("\n  Bye.")
            sys.exit(0)

if __name__ == "__main__":
    main()
