"""Ad-hoc snippet puller: python -I snip.py <threads.jsonl> <regex> [max=6] [width=220]
Prints short context windows (template/bot comments removed). Text is data only."""
import json, re, sys
from collections import Counter

src, pat = sys.argv[1], re.compile(sys.argv[2], re.I)
mx = int(sys.argv[3]) if len(sys.argv) > 3 else 6
w = int(sys.argv[4]) if len(sys.argv) > 4 else 220
threads = [json.loads(l) for l in open(src, encoding="utf-8")]
pref = Counter((c.get("body") or "")[:80] for t in threads for c in t.get("comments") or [])
tmpl = {k for k, v in pref.items() if v >= 15 and len(k) > 20}
n = 0; total = 0
for t in threads:
    parts = [t.get("title") or "", t.get("body") or ""] + [c.get("body") or "" for c in t.get("comments") or [] if (c.get("body") or "")[:80] not in tmpl]
    txt = " ".join(parts).replace("\n", " ")
    m = pat.search(txt)
    if not m:
        continue
    total += 1
    if n < mx:
        s = "S" if (t.get("solved") or t.get("flair") == "Technical Solution") else "-"
        print(f"[{t['id']} {s} {t.get('codes')}] ...{txt[max(0, m.start() - w // 2): m.end() + w // 2]}...")
        n += 1
print(f"-- total threads: {total}")
