"""r/ValorantTechSupport post+yorumlarını Arctic Shift arşivinden toplar (ücretsiz, girişsiz).

Kullanım: python collect.py [--since 2025-10-20] [--until 2026-10-21]
Çıktı: data/posts.jsonl, data/comments.jsonl
"""
import argparse
import json
import sys
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

API = "https://arctic-shift.photon-reddit.com/api"
SUB = "ValorantTechSupport"
OUT = Path(__file__).parent / "data"

POST_FIELDS = ["id", "created_utc", "title", "selftext", "link_flair_text", "num_comments", "score", "permalink", "author"]
COMMENT_FIELDS = ["id", "created_utc", "link_id", "parent_id", "body", "score", "author"]


def get(path, params):
    url = f"{API}/{path}?" + "&".join(f"{k}={v}" for k, v in params.items())
    for attempt in range(6):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "valorant-techsupport-research/0.1"})
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.load(r)["data"]
        except Exception as e:  # rate limit / ağ hatası: bekle ve tekrar dene
            wait = 5 * (attempt + 1)
            print(f"  hata ({e}), {wait}s bekleniyor", file=sys.stderr)
            time.sleep(wait)
    raise RuntimeError(f"başarısız: {url}")


def ts(day):
    return int(datetime.strptime(day, "%Y-%m-%d").replace(tzinfo=timezone.utc).timestamp())


def crawl(kind, fields, since, until, out_file):
    seen, after = set(), since
    with open(out_file, "w", encoding="utf-8") as f:
        while True:
            rows = get(f"{kind}/search", {"subreddit": SUB, "after": after, "before": until, "sort": "asc", "limit": 100})
            new = [r for r in rows if r["id"] not in seen]
            for r in new:
                seen.add(r["id"])
                f.write(json.dumps({k: r.get(k) for k in fields}, ensure_ascii=False) + "\n")
            if len(rows) < 100 or not new:
                break
            after = max(int(r["created_utc"]) for r in rows) - 1
            print(f"{kind}: {len(seen)} ({datetime.fromtimestamp(after, timezone.utc):%Y-%m-%d})", flush=True)
            time.sleep(0.5)
    print(f"{kind} bitti: {len(seen)}", flush=True)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--since", default="2025-10-20")
    ap.add_argument("--until", default="2026-10-21")
    ap.add_argument("--after-ts", type=int, help="--since yerine kesin unix başlangıcı")
    ap.add_argument("--comments-only", metavar="OUTFILE", help="sadece yorumları bu dosyaya topla (paralel parçalar için)")
    a = ap.parse_args()
    OUT.mkdir(exist_ok=True)
    start = a.after_ts or ts(a.since)
    if a.comments_only:
        crawl("comments", COMMENT_FIELDS, start, ts(a.until), OUT / a.comments_only)
    else:
        crawl("posts", POST_FIELDS, start, ts(a.until), OUT / "posts.jsonl")
        crawl("comments", COMMENT_FIELDS, start, ts(a.until), OUT / "comments.jsonl")
