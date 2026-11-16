"""Query Ledger's snapshots and acquisition records. Read-only; every number is computed on read.

  python tools/ledger.py stats                 library breakdown by store / installed / played
  python tools/ledger.py game <name>           one game: store, playtime, last played, acquired, history
  python tools/ledger.py daily [--days N]      playtime per day, from differences between snapshots
  python tools/ledger.py backlog [--store S]   owned but never played, oldest acquisition first
  add --json to any command for machine-readable output (hub, Bridge).

Data dir: $PLAYNITE_DATA or E:/projects/playnite-data.
"""
import argparse
import glob
import json
import os
import sys
from collections import Counter, defaultdict

DATA = os.environ.get("PLAYNITE_DATA", r"E:\projects\playnite-data")
LEDGER = os.path.join(DATA, "ExtensionsData", "0ca0d44d-044a-4eb0-b6b8-863e05a6df89")
SNAPS = os.path.join(LEDGER, "snapshots")


def snapshot_files():
    """Snapshot files in date order. YYYY-MM-DD.jsonl = full; YYYY-MM-DD.d.jsonl = changed rows only."""
    return sorted(glob.glob(os.path.join(SNAPS, "*.jsonl")), key=lambda p: os.path.basename(p)[:10])


def _rows(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def states():
    """Yield (day, full rows) per snapshot day, overlaying deltas on the last full snapshot."""
    state = None
    for p in snapshot_files():
        name = os.path.basename(p)
        if not name.endswith(".d.jsonl"):
            state = {}
        if state is None:
            continue
        for r in _rows(p):
            state[r["id"]] = r
        yield name[:10], list(state.values())


def load_snapshot(path):
    """Full rows as of the snapshot file's day (reconstructed if it's a delta)."""
    day = os.path.basename(path)[:10]
    for d, rows in states():
        if d == day:
            return rows
    return []


def latest():
    all_states = list(states())
    if not all_states:
        sys.exit(f"no snapshots in {SNAPS}. Start Playnite with Ledger installed.")
    day, rows = all_states[-1]
    return rows, day


def acquisitions():
    p = os.path.join(LEDGER, "acquisitions.json")
    if not os.path.exists(p):
        return {}
    with open(p, encoding="utf-8-sig") as f:
        return {r["GameId"]: r for r in json.load(f)}


def first_played():
    """appId -> first unlock ISO date, from the Steam client's local achievement cache."""
    try:
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        import steam_local_stats as sls
        out = {}
        for app, path in sls.all_games():
            try:
                t = sls.unlock_times(sls.load(path))
            except Exception:
                continue
            if t:
                out[str(app)] = sls.iso(min(t))[:10]
        return out
    except Exception:
        return {}


STEAM_PLUGIN = "cb91dfc9-b977-43bf-8e70-55f46e410fab"


def hours(sec):
    return round((sec or 0) / 3600, 1)


def cmd_stats(a):
    rows, day = latest()
    by_store = Counter(r["source"] or "(none)" for r in rows)
    out = {
        "snapshot": day,
        "games": len(rows),
        "byStore": dict(by_store.most_common()),
        "installed": sum(r["installed"] for r in rows),
        "neverPlayed": sum(1 for r in rows if not r["playtimeSec"]),
        "totalHours": hours(sum(r["playtimeSec"] or 0 for r in rows)),
        "acquisitionDatesKnown": len(acquisitions()),
        "firstPlayedKnown": len(first_played()),
        "hiddenDuplicates": sum(1 for r in rows if r["hidden"]),
    }
    return out, lambda o: "\n".join(f"{k:22} {v}" for k, v in o.items())


def cmd_game(a):
    rows, _ = latest()
    q = a.name.lower()
    hits = [r for r in rows if q in r["name"].lower()]
    acq = acquisitions()
    fp = first_played()
    hist = defaultdict(list)
    ids = {r["id"] for r in hits}
    for day, rows in states():
        for r in rows:
            if r["id"] in ids:
                hist[r["id"]].append({"day": day, "hours": hours(r["playtimeSec"])})
    out = []
    for r in hits:
        ac = acq.get(r["id"])
        out.append({
            "name": r["name"], "store": r["source"], "hours": hours(r["playtimeSec"]),
            "lastPlayed": (r["lastActivity"] or "")[:10] or None, "installed": r["installed"],
            "acquired": ac["Acquired"][:10] if ac else None, "license": ac["License"] if ac else None,
            "firstPlayed": fp.get(r["gameId"]) if r["pluginId"] == STEAM_PLUGIN else None,
            "history": hist[r["id"]],
        })

    def fmt(o):
        if not o:
            return "no match"
        lines = []
        for g in o:
            lines.append(f"{g['name']}  [{g['store']}]")
            lines.append(f"  playtime {g['hours']}h · last played {g['lastPlayed'] or 'never'} · installed {g['installed']}")
            lines.append(f"  acquired {g['acquired'] or 'unknown (run Ledger → Import Steam acquisition dates)'}"
                         + (f" via \"{g['license']}\"" if g["license"] else ""))
            if g["firstPlayed"]:
                lines.append(f"  first played {g['firstPlayed']} (earliest Steam achievement)")
            if len(g["history"]) > 1:
                lines.append("  history " + ", ".join(f"{h['day']}={h['hours']}h" for h in g["history"]))
        return "\n".join(lines)
    return out, fmt


def cmd_daily(a):
    days = list(states())[-(a.days + 1):]
    out = []
    prev = None
    for day, rows in days:
        cur = {r["id"]: r for r in rows}
        if prev is not None:
            played = []
            for gid, r in cur.items():
                d = (r["playtimeSec"] or 0) - (prev.get(gid, {}).get("playtimeSec") or 0)
                if d > 0:
                    played.append({"name": r["name"], "hours": hours(d)})
            played.sort(key=lambda x: -x["hours"])
            out.append({"day": day, "hours": round(sum(p["hours"] for p in played), 1), "games": played})
        prev = cur

    def fmt(o):
        if not o:
            return f"need at least two snapshots (have {len(snapshot_files())}). Snapshots accumulate daily."
        return "\n".join(f"{d['day']}  {d['hours']:5}h  " + ", ".join(f"{g['name']} {g['hours']}h" for g in d["games"][:5]) for d in o)
    return out, fmt


def cmd_backlog(a):
    rows, _ = latest()
    acq = acquisitions()
    out = []
    for r in rows:
        if r["playtimeSec"] or r["hidden"]:
            continue
        if a.store and (r["source"] or "").lower() != a.store.lower():
            continue
        ac = acq.get(r["id"])
        out.append({"name": r["name"], "store": r["source"], "acquired": ac["Acquired"][:10] if ac else None, "installed": r["installed"]})
    out.sort(key=lambda x: (x["acquired"] is None, x["acquired"] or "", x["name"]))
    return out, lambda o: f"{len(o)} never played\n" + "\n".join(
        f"{g['acquired'] or '          '}  {g['store'] or '':10} {'*' if g['installed'] else ' '} {g['name']}" for g in o)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--json", action="store_true")
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("stats")
    g = sub.add_parser("game"); g.add_argument("name")
    d = sub.add_parser("daily"); d.add_argument("--days", type=int, default=14)
    b = sub.add_parser("backlog"); b.add_argument("--store")
    a = p.parse_args()
    out, fmt = {"stats": cmd_stats, "game": cmd_game, "daily": cmd_daily, "backlog": cmd_backlog}[a.cmd](a)
    sys.stdout.reconfigure(encoding="utf-8")
    print(json.dumps(out, indent=2, ensure_ascii=False) if a.json else fmt(out))


if __name__ == "__main__":
    main()
