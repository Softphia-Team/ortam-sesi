"""Analyses the output of real-test.ps1: diag CSV (session peak meters) + app log + event times."""
import csv
import os
import re
import sys
from collections import defaultdict

OUT = sys.argv[1]
fails = 0


def check(ok, text):
    global fails
    print(f"  [{'OK ' if ok else 'FAIL'}] {text}")
    if not ok:
        fails += 1


def secs(hms):
    h, m, s = hms.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def load_csv(path):
    series = defaultdict(list)
    with open(path, encoding="utf-8") as f:
        for row in csv.DictReader(f):
            series[row["app"]].append((secs(row["wall"]), float(row["peak_db"])))
    return series


def load_log(path):
    out = []
    if not os.path.exists(path):
        return out
    with open(path, encoding="utf-8") as f:
        for line in f:
            m = re.match(r"\d{4}-\d\d-\d\d (\d\d:\d\d:\d\d\.\d{3})  (.*)", line)
            if m:
                out.append((secs(m.group(1)), m.group(2).strip()))
    return out


def load_events(path):
    ev = {}
    with open(path, encoding="ascii") as f:
        for line in f:
            t, name = line.strip().split(",", 1)
            ev[name] = secs(t)
    return ev


def bursts(points, thr=-60.0, gap=0.6):
    segs, cur = [], None
    for t, db in sorted(points):
        if db > thr:
            if cur and t - cur[1] <= gap:
                cur[1] = t
            else:
                if cur:
                    segs.append(tuple(cur))
                cur = [t, t]
    if cur:
        segs.append(tuple(cur))
    return segs


def window_max(points, a, b):
    vals = [db for t, db in points if a <= t <= b]
    return max(vals) if vals else None


def window_median(points, a, b):
    vals = sorted(db for t, db in points if a <= t <= b)
    return vals[len(vals) // 2] if vals else None


def fmt(v):
    return "none" if v is None else f"{v:.0f} dB"


def timeline(points, t0, t1):
    cells = []
    t = t0
    while t < t1:
        m = window_max(points, t, t + 1)
        cells.append("  ." if m is None or m < -150 else f"{m:3.0f}")
        t += 1
    return " ".join(cells)


ev = load_events(os.path.join(OUT, "events.csv"))

# ---------------- Phase 1 ----------------
if "phase1-start" in ev:
    print("=== Phase 1: sound really playing in Brave ===")
    s = load_csv(os.path.join(OUT, "phase1.csv"))
    log = load_log(os.path.join(OUT, "phase1", "log.txt"))
    t0, t1 = ev["phase1-start"], ev["phase1-end"]
    brave = s.get("brave", [])
    playing = [b for b in bursts(brave) if b[1] > t0 and b[0] < t1]
    print(f"  Brave audio segments: {[(round(a - t0, 1), round(b - t0, 1)) for a, b in playing]}")
    ducks = [(t, m) for t, m in log if m.startswith("DUCKED")]
    if playing:
        check(any("Brave" in m for _, m in ducks), "Brave audio detected, ambient sound ducked")
        if ducks:
            print(f"  first DUCKED: {ducks[0][0] - t0:.2f} s after start -> {ducks[0][1]}")
        own = s.get("ortamsesi", [])
        print(f"  ambient peak (per second): {timeline(own, t0, t1)}")
        mx = window_max(own, t0 + 3, t1 - 1)
        check(mx is not None and mx < -40, f"ambient sound stayed down while Brave played (max {fmt(mx)}; keep-alive floor only)")
    else:
        print("  (Brave was not playing at the time; phase 1 skipped)")
    print()

# ---------------- Phase 2 ----------------
print("=== Phase 2: transitions ===")
s = load_csv(os.path.join(OUT, "phase2.csv"))
log = load_log(os.path.join(OUT, "phase2", "log.txt"))
t0, tend = ev["phase2-start"], ev["phase2-end"]
own = s.get("ortamsesi", [])
ducks = [t for t, m in log if m.startswith("DUCKED")]
unducks = [t for t, m in log if m.startswith("RESUMING")]
for t, m in log:
    if m.startswith(("DUCKED", "RESUMING", "Output", "Started")):
        print(f"  log +{t - t0:6.2f} s: {m}")
print(f"  ambient peak (max per second, dB):\n    {timeline(own, t0, tend + 3)}")

others = {app: pts for app, pts in s.items() if app not in ("ortamsesi", "brave", "system")}
all_bursts = []
for app, pts in others.items():
    for a, b in bursts(pts):
        if t0 <= a <= tend:
            all_bursts.append((a, b, app))
all_bursts.sort()
print("  audio segments of other apps:")
for a, b, app in all_bursts:
    print(f"    {app:12s} +{a - t0:6.2f} .. +{b - t0:6.2f} s  ({b - a + 0.1:.1f} s)")

# Must not detect itself: no ducking while only the ambient sound plays (first 12 s)
check(not [t for t in ducks if t < ev["short-notification-0.5s"]],
      "did not duck itself while only the ambient sound was playing")
lvl_play = window_median(own, t0 + 9, ev["short-notification-0.5s"] - 0.2)
check(lvl_play is not None and lvl_play > -45, f"ambient sound reached its level within 6 s (median peak {fmt(lvl_play)})")

# Short notification
tb = ev["short-notification-0.5s"]
check(not [t for t in ducks if tb <= t <= tb + 4], "0.5 s notification did not duck")

# Chrome, 10 s
tc = ev["chrome-10s-audio"]
cb = [x for x in all_bursts if x[2] == "chrome" and tc <= x[0] <= tc + 15 and x[1] - x[0] > 3]
if cb:
    a, b, _ = cb[0]
    d = [t for t in ducks if a - 0.2 <= t <= b + 1]
    u = [t for t in unducks if t > b]
    if d:
        print(f"    ducked {d[0] - a:.2f} s after Chrome audio started")
    if u:
        print(f"    started coming back {u[0] - b:.2f} s after Chrome audio ended")
    check(bool(d) and 2.0 <= d[0] - a <= 3.6, "ducked ~2.5 s after Chrome audio started")
    check(bool(u) and 4.3 <= u[0] - b <= 7.0, "came back ~5 s after Chrome audio ended")
    lvl_duck = window_median(own, d[0] + 2.5, b) if d else None
    check(lvl_duck is not None and lvl_duck < -45, f"ambient sound went down to the floor while ducked (median peak {fmt(lvl_duck)})")
    lvl_back = window_median(own, u[0] + 9, ev["chrome-muted-tab"]) if u else None
    check(lvl_back is not None and lvl_back > -45, f"returned to its level afterwards (median peak {fmt(lvl_back)})")
else:
    check(False, "could not measure the Chrome test page's audio")

# Chrome open but silent + muted tab
tm, tp = ev["chrome-muted-tab"], ev["other-app-8s-audio"]
chrome_after = [x for x in all_bursts if x[2] == "chrome" and tm - 12 <= x[0] <= tp]
check(not chrome_after, "meter stayed at 0 while Chrome was open but silent / the tab was muted")
check(not [t for t in ducks if tm - 12 <= t <= tp], "silent / muted Chrome did not duck")

# Other app, 8 s
pb = [x for x in all_bursts if x[2] != "chrome" and tp - 0.5 <= x[0] <= tp + 3]
if pb:
    a, b, app = pb[0]
    d = [t for t in ducks if a - 0.2 <= t <= b + 1]
    u = [t for t in unducks if t > b]
    check(bool(d) and 2.0 <= d[0] - a <= 3.6, f"ducked when another app ({app}) played ({(d[0] - a) if d else float('nan'):.2f} s)")
    check(bool(u) and 4.3 <= u[0] - b <= 7.0, f"came back after it ended ({(u[0] - b) if u else float('nan'):.2f} s)")
else:
    check(False, "could not measure the other app's audio")

check(len(ducks) == 2, f"ducked exactly twice (Chrome + other app): {len(ducks)}")
print()
print("RESULT:", "all checks passed" if fails == 0 else f"{fails} checks failed")
sys.exit(1 if fails else 0)
