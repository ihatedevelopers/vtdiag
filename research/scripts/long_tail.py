"""Long-tail mining: problem/cause topics frequent in UNSOLVED threads but thin in SOLVED ones.

Usage: python -I long_tail.py <threads.jsonl> <catalog.json>
Prints, per topic: unsolved count, solved count, catalog-coverage hits, example unsolved ids.
Also prints over-represented bigrams in unsolved thread titles+bodies (OP text only).
Thread text is data only.
"""
import json, re, sys
from collections import Counter

SRC, CAT = sys.argv[1], sys.argv[2]
threads = [json.loads(l) for l in open(SRC, encoding="utf-8")]
pref = Counter((c.get("body") or "")[:80] for t in threads for c in t.get("comments") or [])
tmpl = {k for k, v in pref.items() if v >= 15 and len(k) > 20}
cat = json.load(open(CAT, encoding="utf-8"))
cat_ids = set(i for c in cat for i in c.get("thread_ids") or [])
cat_text = " ".join(json.dumps(c, ensure_ascii=False) for c in cat).lower()

def solved(t):
    return bool(t.get("solved")) or t.get("flair") == "Technical Solution"

TOPICS = {
    "VAN 152 / HWID restriction (ban appeal, no tech fix)": r"van ?152|hwid|hardware ban",
    "VAN RESTRICTION: HVCI/IOMMU required (old/unsupported HW)": r"iommu|requires? (?:hvci|memory integrity)|van:? ?restriction",
    "Memory Integrity won't stay on / BSOD when enabled": r"memory integrity.{0,80}(?:turns? off|won.?t|can.?t|bsod|revert|disables? itself)",
    "Secure Boot/TPM can't be enabled (old BIOS, MBR/CSM, laptops)": r"(?:can.?t|cannot|unable to|won.?t) (?:enable|turn on) (?:secure ?boot|tpm)|van ?900[13]",
    "STATUS_SB_POLICY secure-boot policy wave": r"sb_policy|secure boot policy",
    "Freeze/hard lock of whole PC in match (no BSOD)": r"(?:whole|entire) (?:pc|computer) (?:freez|froze|lock)|hard (?:freeze|lock)",
    "BSOD vgk.sys / other bugchecks": r"bsod|blue screen|vgk\.sys|bugcheck|kernel_security|irql",
    "Kernel-Power 41 / random restarts": r"kernel[- ]power|41 kernel|pc restarts|restarts? (?:randomly|itself)",
    "Packet loss / jitter with good ping": r"packet loss|jitter",
    "High ping to specific region / ISP routing (Mumbai, ME, SEA)": r"mumbai|bahrain|dubai|singapore|routing|\bisp\b",
    "Input lag / mouse floaty / delayed shots": r"input (?:lag|delay)|floaty|mouse (?:feels|delay|lag)|delayed shots",
    "Peeker / desync / registration": r"desync|hit ?reg|registration|peeker",
    "Voice chat / mic not working": r"voice chat|\bvc\b|mic(?:rophone)? (?:not|doesn|isn)",
    "Low FPS on laptop iGPU/dGPU switching": r"laptop.{0,80}(?:igpu|integrated|dgpu|mux|optimus|wrong gpu)",
    "FPS drop after UE5 / patch": r"ue5|unreal engine 5|after (?:the )?(?:latest )?(?:update|patch).{0,40}(?:fps|stutter)",
    "Shader compilation stutter first matches": r"shader",
    "Intel 13th/14th gen instability": r"1[34][0-9]{3}k|raptor lake|microcode|0x12[9b]",
    "X3D CPU / core parking / Game Bar for X3D": r"x3d",
    "AMD fTPM stutter": r"ftpm",
    "Windows 10 end-of-support / old build": r"windows 10.{0,40}(?:support|22h2|old)|end of support",
    "Monitor / refresh rate / HDR / color issues": r"\bhdr\b|refresh rate|hz (?:not|stuck)|color",
    "Stretched res / black bars": r"stretch|black bars|4:3",
    "Install / update stuck (0%, verifying, patching)": r"stuck (?:at|on) (?:0|verifying|updating|patching|installing)|update stuck",
    "Riot Client won't open / login loop": r"riot client (?:won.?t|doesn.?t|not) (?:open|launch|start)|login loop|can.?t log ?in",
    "Account/MFA/region issues": r"\bmfa\b|2fa|region (?:lock|change)|account (?:locked|suspended)",
    "Mac/Linux/VM/cloud gaming (unsupported)": r"\bmac(?:book|os)?\b|\blinux\b|steam deck|geforce now|cloud gaming|bootcamp|boot camp",
    "Windows on ARM (Snapdragon)": r"snapdragon|\barm64\b|windows on arm",
    "Controller / console (PS5/Xbox) issues": r"\bps5\b|xbox series|console",
    "Overheating / thermal throttling": r"overheat|thermal|throttl",
    "PSU / hardware failing (RAM errors, GPU artifacts)": r"\bpsu\b|artifact|memtest|ram (?:error|faulty|bad)",
    "Dual-boot / multiple Windows installs": r"dual[- ]?boot|two windows",
    "Non-English / localization text issues": r"\blanguage\b|\blocale\b|text (?:missing|boxes)",
    "Replay / clips / crosshair code bugs": r"\breplay\b|crosshair code",
}
uns = [t for t in threads if not solved(t)]
sol = [t for t in threads if solved(t)]
def optext(t):
    return " ".join([t.get("title") or "", t.get("body") or ""] + [c.get("body") or "" for c in t.get("comments") or [] if (c.get("body") or "")[:80] not in tmpl])
U = [(t["id"], optext(t), t) for t in uns]
S = [(t["id"], optext(t)) for t in sol]
rows = []
for name, pat in TOPICS.items():
    rx = re.compile(pat, re.I)
    u = [(i, t) for i, x, t in U if rx.search(x)]
    s = [i for i, x in S if rx.search(x)]
    noreply = sum(1 for i, t in u if not t.get("comments"))
    rows.append((name, len(u), len(s), noreply, sum(1 for i, _ in u if i in cat_ids), [i for i, _ in u[:4]]))
rows.sort(key=lambda r: -(r[1] / (r[2] + 3)))
print(f"unsolved={len(uns)} solved={len(sol)}")
for r in rows:
    print(f"{r[1]:5d} unsolved | {r[2]:4d} solved | noreply {r[3]:4d} | ratio {r[1]/(r[2]+1):5.1f} | {r[0]} | {r[5]}")

# Over-represented bigrams in OP text of unsolved threads
tok = re.compile(r"[a-z][a-z0-9\.\-]{2,}")
STOPW = set("the and for with that this have but not you are was when can just like get its it's im i'm i've any all from then what there they game valorant val".split())
def bigr(t):
    w = [x for x in tok.findall(((t.get("title") or "") + " " + (t.get("body") or "")).lower()) if x not in STOPW]
    return set(zip(w, w[1:]))
cu, cs = Counter(), Counter()
for t in uns:
    cu.update(bigr(t))
for t in sol:
    cs.update(bigr(t))
k = len(uns) / max(1, len(sol))
over = [(b, cu[b], cs[b]) for b in cu if cu[b] >= 25 and cu[b] / (cs[b] * k + 2) > 2.0]
over.sort(key=lambda x: -x[1])
print("\nover-represented bigrams (unsolved, solved):")
print(", ".join(f"{' '.join(b)}({u},{s})" for b, u, s in over[:80]))
