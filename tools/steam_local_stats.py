"""Read achievement unlock times from the Steam client's local cache. No API key, no network.

Steam keeps, per account and game, `appcache/stats/UserGameStats_<accountId>_<appId>.bin`
(binary KeyValues). Achievement stats carry an `AchievementTimes` block: bit -> unix time.

  python tools/steam_local_stats.py dump <appId>        raw tree for one game
  python tools/steam_local_stats.py first [--json]      earliest/latest unlock per game

Only covers achievements this PC's Steam client has synced, which in practice is every game
you've launched on this account here (Steam pulls the stats file on launch).
"""
import argparse
import datetime as dt
import glob
import json
import os
import re
import struct
import sys

STEAM = os.environ.get("STEAM_DIR", r"C:\Program Files (x86)\Steam")
STEAM_ID64 = int(os.environ.get("STEAM_ID", "76561198193635639"))
ACCOUNT_ID = STEAM_ID64 - 76561197960265728
STATS = os.path.join(STEAM, "appcache", "stats")


def read_kv(buf, i=0):
    """Binary KeyValues -> dict. Returns (dict, next_index)."""
    out = {}
    while i < len(buf):
        t = buf[i]
        i += 1
        if t == 8:  # end of object
            return out, i
        end = buf.index(b"\0", i)
        key = buf[i:end].decode("utf-8", "replace")
        i = end + 1
        if t == 0:
            out[key], i = read_kv(buf, i)
        elif t == 1:
            end = buf.index(b"\0", i)
            out[key] = buf[i:end].decode("utf-8", "replace")
            i = end + 1
        elif t in (2, 4, 6):  # int32, ptr, color
            out[key] = struct.unpack_from("<i", buf, i)[0]
            i += 4
        elif t == 3:
            out[key] = struct.unpack_from("<f", buf, i)[0]
            i += 4
        elif t in (7, 10):  # uint64, int64
            out[key] = struct.unpack_from("<Q" if t == 7 else "<q", buf, i)[0]
            i += 8
        else:
            raise ValueError(f"unknown kv type {t} at {i - 1}")
    return out, i


def load(path):
    with open(path, "rb") as f:
        return read_kv(f.read())[0]


def unlock_times(tree):
    """All unix times found under any AchievementTimes block."""
    times = []

    def walk(node):
        if isinstance(node, dict):
            for k, v in node.items():
                if k == "AchievementTimes" and isinstance(v, dict):
                    times.extend(t for t in v.values() if isinstance(t, int) and t > 1_000_000_000)
                else:
                    walk(v)
    walk(tree)
    return times


def all_games():
    rx = re.compile(rf"UserGameStats_{ACCOUNT_ID}_(\d+)\.bin$")
    for p in glob.glob(os.path.join(STATS, f"UserGameStats_{ACCOUNT_ID}_*.bin")):
        m = rx.search(p)
        if m:
            yield int(m.group(1)), p


def iso(t):
    return dt.datetime.fromtimestamp(t, dt.timezone.utc).isoformat()


def cmd_first(a):
    rows = []
    for app, p in all_games():
        try:
            times = unlock_times(load(p))
        except Exception as e:  # one bad file shouldn't stop the rest
            print(f"skip {app}: {e}", file=sys.stderr)
            continue
        if times:
            rows.append({"appId": app, "unlocked": len(times), "first": iso(min(times)), "last": iso(max(times))})
    rows.sort(key=lambda r: r["first"])
    if a.json:
        print(json.dumps(rows, indent=2))
    else:
        for r in rows:
            print(f"{r['first'][:10]}  {r['last'][:10]}  {r['unlocked']:4}  {r['appId']}")
        print(f"{len(rows)} games with unlock times", file=sys.stderr)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    d = sub.add_parser("dump"); d.add_argument("app", type=int)
    f = sub.add_parser("first"); f.add_argument("--json", action="store_true")
    a = p.parse_args()
    if a.cmd == "dump":
        print(json.dumps(load(os.path.join(STATS, f"UserGameStats_{ACCOUNT_ID}_{a.app}.bin")), indent=1)[:3000])
    else:
        cmd_first(a)


if __name__ == "__main__":
    main()
