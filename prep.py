"""posts+comments -> threads.jsonl, yerel istatistik (stats.json) ve çözülmüş thread parçaları (chunks/)."""
import json
import re
from collections import Counter, defaultdict
from pathlib import Path

D = Path(__file__).parent / "data"
posts = [json.loads(l) for l in open(D / "posts.jsonl", encoding="utf-8")]
comments = [json.loads(l) for l in open(D / "comments.jsonl", encoding="utf-8")]

by_post = defaultdict(list)
for c in comments:
    by_post[(c["link_id"] or "").removeprefix("t3_")].append(c)

SOLVED = re.compile(r"\b(fixed|solved|that worked|it worked|this worked|works now|working now|thank(s| you)[^.]{0,40}(work|fix)|resolved)\b", re.I)
CODE = re.compile(r"\b(VAN|VAL)\s?-?\s?(\d{1,4})\b", re.I)
KEYWORDS = {
    "vanguard": r"vanguard|vgc|vgk",
    "fps_drop": r"fps (drop|dip)|stutter|frame ?drop|low fps|fps loss",
    "crash": r"crash|freez|bsod|blue screen|black screen",
    "network": r"ping|packet loss|lag|network problem|jitter|rubber ?band",
    "tpm_secureboot": r"tpm|secure ?boot|uefi|csm|hvci|memory integrity",
    "install_update": r"install|update|download|patch|repair|corrupt",
    "login_account": r"login|log in|riot client|account|banned|ban\b",
    "audio_mic": r"audio|sound|mic\b|microphone|voice chat",
    "input_mouse": r"input delay|input lag|mouse|keyboard|controller",
    "display": r"resolution|stretched|monitor|hz\b|refresh rate|fullscreen|windowed",
    "gpu": r"nvidia|amd|radeon|geforce|driver",
}

stats = Counter()
codes = Counter()
flairs = Counter()
monthly = Counter()
threads = []
for p in posts:
    text = f"{p['title']}\n{p.get('selftext') or ''}"
    cs = sorted(by_post.get(p["id"], []), key=lambda c: c["created_utc"])
    op_replies = [c["body"] for c in cs if c["author"] == p["author"] and p["author"] != "[deleted]"]
    solved = bool(SOLVED.search(" ".join(op_replies))) or bool(re.search(r"\b(fixed|solved)\b", p.get("link_flair_text") or "", re.I))
    found = {f"{m.group(1).upper()} {m.group(2)}" for m in CODE.finditer(text)}
    codes.update(found)
    for k, rx in KEYWORDS.items():
        if re.search(rx, text, re.I):
            stats[k] += 1
    flairs[p.get("link_flair_text")] += 1
    from datetime import datetime, timezone
    monthly[datetime.fromtimestamp(p["created_utc"], timezone.utc).strftime("%Y-%m")] += 1
    threads.append({
        "id": p["id"], "title": p["title"], "body": (p.get("selftext") or "")[:1500], "flair": p.get("link_flair_text"),
        "codes": sorted(found), "solved": solved, "n_comments": len(cs),
        # yazar adı yerine OP işareti: kişisel veri tutmuyoruz
        "comments": [{"op": c["author"] == p["author"], "score": c["score"], "body": (c["body"] or "")[:800]} for c in cs if c["body"] not in ("[deleted]", "[removed]")][:25],
    })

with open(D / "threads.jsonl", "w", encoding="utf-8") as f:
    for t in threads:
        f.write(json.dumps(t, ensure_ascii=False) + "\n")

# çözüm içeren thread'ler: OP "düzeldi" demiş, ya da oyuncu çözüm rehberi paylaşmış
solved = [t for t in threads if t["solved"] or t["flair"] == "Technical Solution"]
(D / "chunks").mkdir(exist_ok=True)
for old in (D / "chunks").glob("*.jsonl"):
    old.unlink()
SIZE = 120
for i in range(0, len(solved), SIZE):
    with open(D / "chunks" / f"solved_{i // SIZE:02d}.jsonl", "w", encoding="utf-8") as f:
        for t in solved[i:i + SIZE]:
            f.write(json.dumps(t, ensure_ascii=False) + "\n")

summary = {
    "posts": len(posts), "comments": len(comments), "solved_threads": len(solved),
    "no_reply_threads": sum(1 for t in threads if t["n_comments"] == 0),
    "keywords": stats.most_common(), "error_codes": codes.most_common(40),
    "flairs": flairs.most_common(), "monthly": sorted(monthly.items()),
}
json.dump(summary, open(D / "stats.json", "w", encoding="utf-8"), indent=1, ensure_ascii=False)
print(json.dumps(summary, indent=1, ensure_ascii=False))
