#!/bin/bash
# Prints the ChatGPT-plan Codex usage recorded by the most recent Codex session,
# and exits 1 when either window is at or above the stop threshold (default 80%).
# Usage: codex-usage.sh [threshold]
t=${1:-80}
# The newest session that carries a rate-limit record (a refused or aborted call records none).
f=$(find ~/.codex/sessions -name '*.jsonl' -print0 2>/dev/null | xargs -0 /bin/ls -t 2>/dev/null | head -40 | while read -r x; do grep -q '"rate_limits":{"limit_id":"codex"' "$x" && { echo "$x"; break; }; done)
[ -n "$f" ] || { echo "no codex session found"; exit 0; }
line=$(grep -o '"rate_limits":{"limit_id":"codex".\{0,250\}' "$f" | tail -1)
[ -n "$line" ] || { echo "no rate-limit record in latest session"; exit 0; }
python3 - "$t" "$line" <<'PY'
import sys, json, re, datetime, zoneinfo
t = float(sys.argv[1]); s = sys.argv[2]
p = re.search(r'"primary":\{"used_percent":([\d.]+),"window_minutes":\d+,"resets_at":(\d+)', s)
w = re.search(r'"secondary":\{"used_percent":([\d.]+),"window_minutes":\d+,"resets_at":(\d+)', s)
syd = zoneinfo.ZoneInfo("Australia/Sydney")
def fmt(m, name):
    pct, ts = float(m.group(1)), int(m.group(2))
    if ts <= datetime.datetime.now().timestamp():
        return 0.0, f"{name} reset since the last run (was {pct:.0f}%)"
    return pct, f"{name} {pct:.0f}% (resets {datetime.datetime.fromtimestamp(ts, syd):%a %d %b %H:%M} Sydney)"
out = []; stop = False
for m, n in ((p, "5-hour"), (w, "weekly")):
    if m:
        pct, txt = fmt(m, n); out.append(txt); stop |= pct >= t
print("codex usage: " + "; ".join(out) + (f" -> STOP (>= {t:.0f}%)" if stop else " -> ok"))
sys.exit(1 if stop else 0)
PY
