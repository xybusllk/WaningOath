#!/usr/bin/env python3
"""Small live-server smoke test for the WaningOath API.

Starts the Release server on a throwaway store, exercises the main economy
paths, and removes the store afterwards. Run from the game root:
    python tools/api-smoke.py
"""
from __future__ import annotations

import json
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SERVER = ROOT / "WaningOath.Server" / "bin" / "Release" / "net9.0" / "WaningOath.Server.exe"
STORE = ROOT / "sim-out" / "api-smoke-players"
AUDIT = ROOT / "sim-out" / "api-smoke-audit.jsonl"
PORT = 8770
BASE = f"http://127.0.0.1:{PORT}/api/game"
USER = "apis"


def call(path: str, body=None, admin=False):
    headers = {"X-WO-User": USER, "Content-Type": "application/json"}
    if admin:
        headers["X-WO-Role"] = "admin"
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, headers=headers,
                                 method="POST" if body is not None else "GET")
    try:
        with urllib.request.urlopen(req, timeout=10) as res:
            return res.status, json.load(res)
    except urllib.error.HTTPError as exc:
        return exc.code, json.loads(exc.read().decode("utf-8"))


def must(condition, label):
    if not condition:
        raise SystemExit("FAIL: " + label)
    print("PASS:", label)


def main():
    if not SERVER.exists():
        raise SystemExit(f"server not found: {SERVER}")
    if STORE.exists():
        shutil.rmtree(STORE, ignore_errors=True)
    AUDIT.unlink(missing_ok=True)

    proc = subprocess.Popen(
        [str(SERVER), "--port", str(PORT), "--store", str(STORE), "--audit", str(AUDIT)],
        cwd=str(ROOT), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    try:
        for _ in range(40):
            try:
                code, _ = call("/state")
                if code == 200:
                    break
            except Exception:
                pass
            time.sleep(0.25)
        else:
            raise SystemExit("FAIL: server did not start")

        must(call("/state")[0] == 200, "state")
        must(call("/dev/unlock-all", {})[1].get("ok"), "unlock all")
        must(call("/team", {
            "team": ["char-shouyueren", "char-lin-jingyuan"],
            "modes": {"char-shouyueren": "winderosion", "char-lin-jingyuan": "lightning"},
        })[1].get("ok"), "team modes")
        state = call("/state")[1]["save"]
        must(state.get("teamModes", {}).get("char-shouyueren") == "winderosion", "team mode persisted")

        code, grant = call("/admin/grant", {
            "userIds": [USER],
            "grant": {"protocolCode": 20000, "receipt": 1000, "passReceipt": 1000, "stamina": 1000},
        }, admin=True)
        must(code == 200 and grant.get("ok"), "admin grant")

        for stage_id in ("stage-core-fire", "stage-core-ice", "stage-core-lightning"):
            code, cleared = call("/stage/clear", {"stageId": stage_id, "outcome": "AllyWin"})
            must(code == 200 and cleared.get("ok"), "stage clear " + stage_id)

        code, daily = call("/daily/claim", {})
        must(code == 200 and daily.get("ok"), "daily delegate claim")
        must(call("/daily/claim", {})[0] == 409, "daily delegate repeat rejected")

        code, freeexp = call("/daily/free-exp", {})
        must(code == 200 and freeexp.get("ok"), "daily free exp")
        must(call("/daily/free-exp", {})[0] == 409, "daily free exp repeat rejected")

        code, boss = call("/stage/clear", {"stageId": "stage-weekly-boss", "outcome": "AllyWin"})
        must(code == 200 and boss.get("result", {}).get("protocolCode") == 60, "weekly boss pays 60")
        must(call("/stage/clear", {"stageId": "stage-weekly-boss", "outcome": "AllyWin"})[0] == 409,
             "weekly boss same-week reject")

        code, gacha = call("/gacha", {"pool": "weapon", "count": 1})
        if code != 200 or not gacha.get("ok"):
            raise SystemExit(f"FAIL: weapon gacha -> {code} {gacha}")
        print("PASS: weapon gacha")
        must(gacha.get("result", {}).get("receiptGained", {}).get("application", 0) >= 6,
             "gacha receipts")

        code, credit = call("/shop/exchange", {"itemId": "credit", "count": 1})
        must(code == 200 and credit.get("ok"), "shop credit exchange")
        call("/admin/grant", {"userIds": [USER], "grant": {"receipt": 100000}}, admin=True)
        must(call("/shop/exchange", {"itemId": "jumpCore", "count": 48})[0] == 200,
             "shop monthly limit first 48")
        must(call("/shop/exchange", {"itemId": "jumpCore", "count": 1})[0] == 400,
             "shop monthly limit 49th rejected")

        state = call("/state")[1]["save"]
        code, _ = call("/save", state)
        must(code == 200, "save round-trip")
        bad = json.loads(json.dumps(state))
        bad["team"] = ["char-nope"]
        must(call("/save", bad)[0] == 400, "invalid team save rejected")
        print("\nAPI smoke passed.")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
        shutil.rmtree(STORE, ignore_errors=True)
        AUDIT.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
