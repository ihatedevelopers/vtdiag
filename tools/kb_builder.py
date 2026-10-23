#!/usr/bin/env python3
"""VTDiag knowledge-base builder (stdlib only; run with `python -I tools/kb_builder.py`).

Sources:   data/catalog.json (90 problems), research/conflicts.json (113), research/error_codes.json (57 codes)
Curation:  tools/kb_curation/*.py (hand-tuned rules, fixes, guides, questions, codes, conflicts overrides, strings)
Turkish:   tools/kb_src/strings_tr.json (+ the Turkish half of every guide text and manual fix step)
Release:   tools/kb_src/release.json (version, createdUtc, minApp, channel, revocation serial)
Output:    kb/ (except kb/schema/, which is generated from the C# contracts by the lead),
           docs/MANUAL_FIXES.md and docs/MANUAL_FIXES.tr.md (every fix done by hand, from kb_curation/manual_steps.py)

Commands:
  build                      regenerate kb/ and kb.manifest.json (default)
  check                      build into memory and fail if kb/ differs (CI drift check)
  keygen --out FILE          make an Ed25519 key pair; FILE gets the private seed (NEVER commit it)
  sign --key FILE            sign kb.manifest.json and kb.revocations.json (writes the .sig files)
  tr-missing --out FILE      write {key: English} for strings that have no Turkish yet
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
KB = ROOT / "kb"
CUR = ROOT / "tools" / "kb_curation"
SRC = ROOT / "tools" / "kb_src"

GUARD_IDS = {
    "riot.game_running", "riot.client_running", "os.pending_reboot", "os.wu_installing", "power.on_ac",
    "power.battery_above", "disk.free_mb_above", "monitor.active", "adapter.not_default_route",
    "connectivity.other_adapter_online", "connectivity.internet", "service.exists", "sec.bitlocker_on",
    "os.modified_windows", "os.clock_ok",
}
ALLOWED_REG_ROOTS = [
    "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
    "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
    "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options",
    "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags",
    "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion",
    "SOFTWARE\\Microsoft\\Windows Defender",
    "SOFTWARE\\Policies\\Microsoft",
    "SOFTWARE\\Microsoft\\WindowsSelfHost",
    "SOFTWARE\\Microsoft\\Windows\\Dwm",
    "SOFTWARE\\Riot Games",
    "SYSTEM\\CurrentControlSet\\Control\\DeviceGuard",
    "SYSTEM\\CurrentControlSet\\Control\\CI",
    "SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers",
    "SYSTEM\\CurrentControlSet\\Control\\SecureBoot",
    "SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power",
    "SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management",
    "SYSTEM\\CurrentControlSet\\Services",
    "HKCU:Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings",
    "HKCU:Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR",
    "HKCU:Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags",
    "HKCU:Software\\Microsoft\\DirectX",
    "HKCU:Software\\Microsoft\\GameBar",
    "HKCU:System\\GameConfigStore",
]
DENIED_REG = [
    "SAM", "SECURITY", "SYSTEM\\CurrentControlSet\\Control\\Lsa", "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon",
    "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList", "SOFTWARE\\Microsoft\\Cryptography",
    "SOFTWARE\\Riot Games\\Riot Vanguard\\Keys", "HKCU:Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\\Passport",
]
SUFFIX_ENTITY = {
    "running": "process", "service_present": "service", "service_running": "service", "driver_loaded": "driver",
    "installed": "app", "adapter_present": "adapter", "adapter_enabled": "adapter", "audio_endpoint": "audioEndpoint",
    "feature_enabled": "windowsFeature", "registry_present": "registryValue", "file_present": "file", "crash_module": None,
    "any": None,
}
SUFFIX_PHRASE = {
    "installed": "whether {name} is installed", "running": "whether {name} is running",
    "service_present": "whether the {name} service exists", "service_running": "whether the {name} service is running",
    "driver_loaded": "whether the {name} driver is loaded", "adapter_present": "whether a {name} network adapter exists",
    "adapter_enabled": "whether the {name} network adapter is on", "crash_module": "whether VALORANT crashed inside {name}",
    "audio_endpoint": "whether a {name} sound device exists", "feature_enabled": "whether the {name} Windows feature is on",
    "registry_present": "the {name} registry setting", "file_present": "the {name} files", "any": "whether {name} is present",
}


# ------------------------------------------------------------------------------------------------------------ io
def load_module(name: str) -> dict:
    path = CUR / f"{name}.py"
    ns: dict = {"__name__": f"kb_curation_{name}", "__file__": str(path)}
    exec(compile(path.read_text(encoding="utf-8"), str(path), "exec"), ns)  # trusted repo file
    return ns


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def dumps(obj) -> str:
    return json.dumps(obj, ensure_ascii=False, indent=2) + "\n"


def drop_none(obj):
    if isinstance(obj, dict):
        return {k: drop_none(v) for k, v in obj.items() if v is not None}
    if isinstance(obj, list):
        return [drop_none(v) for v in obj]
    return obj


class Errors(list):
    def add(self, msg: str):
        self.append(msg)


# ------------------------------------------------------------------------------------------------------------ ed25519
# RFC 8032 section 6 reference arithmetic (signing only happens here; verification is in VTD.Knowledge).
_P = 2 ** 255 - 19
_L = 2 ** 252 + 27742317777372353535851937790883648493
_D = -121665 * pow(121666, _P - 2, _P) % _P
_SQRT_M1 = pow(2, (_P - 1) // 4, _P)


def _padd(a, b):
    A = (a[1] - a[0]) * (b[1] - b[0]) % _P
    B = (a[1] + a[0]) * (b[1] + b[0]) % _P
    C = 2 * a[3] * b[3] * _D % _P
    Dd = 2 * a[2] * b[2] % _P
    E, F, G, H = B - A, Dd - C, Dd + C, B + A
    return (E * F % _P, G * H % _P, F * G % _P, E * H % _P)


def _pmul(s, pt):
    q = (0, 1, 1, 0)
    while s > 0:
        if s & 1:
            q = _padd(q, pt)
        pt = _padd(pt, pt)
        s >>= 1
    return q


def _recover_x(y, sign):
    if y >= _P:
        return None
    x2 = (y * y - 1) * pow(_D * y * y + 1, _P - 2, _P)
    if x2 == 0:
        return None if sign else 0
    x = pow(x2, (_P + 3) // 8, _P)
    if (x * x - x2) % _P != 0:
        x = x * _SQRT_M1 % _P
    if (x * x - x2) % _P != 0:
        return None
    if (x & 1) != sign:
        x = _P - x
    return x


_GY = 4 * pow(5, _P - 2, _P) % _P
_GX = _recover_x(_GY, 0)
_G = (_GX, _GY, 1, _GX * _GY % _P)


def _penc(pt):
    zinv = pow(pt[2], _P - 2, _P)
    x, y = pt[0] * zinv % _P, pt[1] * zinv % _P
    return int.to_bytes(y | ((x & 1) << 255), 32, "little")


def _sha512_int(*parts: bytes) -> int:
    return int.from_bytes(hashlib.sha512(b"".join(parts)).digest(), "little")


def _expand(seed: bytes):
    h = hashlib.sha512(seed).digest()
    a = int.from_bytes(h[:32], "little")
    a &= (1 << 254) - 8
    a |= 1 << 254
    return a, h[32:]


def ed25519_public(seed: bytes) -> bytes:
    a, _ = _expand(seed)
    return _penc(_pmul(a, _G))


def ed25519_sign(seed: bytes, msg: bytes) -> bytes:
    a, prefix = _expand(seed)
    pub = _penc(_pmul(a, _G))
    r = _sha512_int(prefix, msg) % _L
    R = _penc(_pmul(r, _G))
    h = _sha512_int(R, pub, msg) % _L
    s = (r + h * a) % _L
    return R + int.to_bytes(s, 32, "little")


def key_id(pub: bytes) -> str:
    return "k-" + hashlib.sha256(pub).hexdigest()[:16]


# ------------------------------------------------------------------------------------------------------------ codes
def normalize_code(text: str) -> str:
    s = text.strip().upper().replace(":", " ")
    s = re.sub(r"\s+", " ", s)
    s = re.sub(r"^(VAN|VAL)(?=\d)", r"\1 ", s)
    return s


def code_slug(code: str) -> str:
    s = code.lower().replace("-", "m").replace("_", "-").replace(" ", "-")
    return re.sub(r"[^a-z0-9-]", "", s)


def parse_code_row(raw: str) -> list[str]:
    """'VAL 8-21 (... 9 and 19 not on page)' -> VAL 8..21 minus 9, 19; 'VAL 52 / 53' -> VAL 52, VAL 53."""
    note = re.search(r"\(([^)]*)\)", raw)
    base = re.sub(r"\([^)]*\)", "", raw).strip()
    skip = set()
    if note and "not on page" in note.group(1):
        skip = {int(n) for n in re.findall(r"\d+", note.group(1))}
    out: list[str] = []
    prefix = None
    for part in [p.strip() for p in base.split("/")]:
        m = re.match(r"^(VAN|VAL)?\s*:?\s*(.*)$", part, re.I)
        pre, rest = (m.group(1) or "").upper(), m.group(2).strip()
        if pre:
            prefix = pre
        rng = re.match(r"^(\d+)-(\d+)$", rest)
        if rng:
            for n in range(int(rng.group(1)), int(rng.group(2)) + 1):
                if n not in skip:
                    out.append(f"{prefix} {n}")
            continue
        out.append(normalize_code(f"{prefix} {rest}" if prefix else rest))
    return out


# ------------------------------------------------------------------------------------------------------------ build
class Builder:
    def __init__(self):
        self.err = Errors()
        self.en: dict[str, str] = {}
        self.tr: dict[str, str] = {}
        self.files: dict[str, object] = {}  # relative path -> json object
        self.catalog = read_json(ROOT / "data" / "catalog.json")
        self.conflicts_src = read_json(ROOT / "research" / "conflicts.json")
        self.codes_src = read_json(ROOT / "research" / "error_codes.json")
        self.ops = {o["op"]: o for o in read_json(KB / "schema" / "ops.json")}
        self.release = read_json(SRC / "release.json")
        tr_path = SRC / "strings_tr.json"
        self.tr_src: dict[str, str] = read_json(tr_path) if tr_path.exists() else {}
        self.cur = {n: load_module(n) for n in ("signals", "symptoms", "questions", "fixes", "rules", "conflicts",
                                                "codes", "collectors", "ui_strings", "evidence_strings", "manual_steps")}
        self.guides_src = []
        self.tables_src = []
        for n in ("guides_a", "guides_b", "guides_c"):
            if (CUR / f"{n}.py").exists():
                ns = load_module(n)
                self.guides_src += ns.get("GUIDES", [])
                self.tables_src += ns.get("TABLES", [])
        self.signals: dict[str, dict] = {}
        self.rules: dict[str, dict] = {}
        self.fix_ids: set[str] = set()
        self.guide_ids: set[str] = set()

    # ---- strings
    def s(self, key: str, text, tr: str | None = None) -> str:
        if isinstance(text, tuple):
            text, tr = text
        if not text or not str(text).strip():
            self.err.add(f"empty English text for {key}")
        if key in self.en and self.en[key] != text:
            self.err.add(f"string key {key} defined twice with different text")
        self.en[key] = text
        if tr:
            self.tr[key] = tr
        return key

    # ---- signals
    def sig(self, sid: str, source: str, title: str, entity=None, admin=False, from_collector=None, question=None):
        if sid in self.signals:
            self.err.add(f"signal {sid} declared twice")
        tk = self.s(f"signal.{sid}.title", title)
        self.signals[sid] = drop_none({"id": sid, "source": source, "titleKey": tk, "entity": entity,
                                       "requiresAdmin": admin or None, "fromCollector": from_collector, "question": question})

    def build(self):
        self.build_symptoms()
        self.build_questions()
        self.build_builtin_signals()
        self.build_collectors()
        self.build_conflicts()
        self.build_fixes()
        self.build_guides()
        self.build_rules()
        self.build_codes()
        self.build_ui_strings()
        self.lint()
        self.files["strings/en.json"] = dict(sorted(self.en.items()))
        tr = {}
        for k in sorted(self.en):
            if k in self.tr:
                tr[k] = self.tr[k]
            elif k in self.tr_src:
                tr[k] = self.tr_src[k]
        self.files["strings/tr.json"] = tr
        self.files["trusted_helpers.json"] = []
        return self

    def build_symptoms(self):
        out = []
        for sid, order, cats, text in self.cur["symptoms"]["SYMPTOMS"]:
            out.append({"id": sid, "titleKey": self.s(f"symptom.{sid}", text), "categories": cats, "order": order})
        self.symptom_ids = {s["id"] for s in out}
        self.symptom_cats = {s["id"]: s["categories"] for s in out}
        self.files["symptoms.json"] = out

    def build_questions(self):
        out = []
        self.s("answer.yes", "Yes")
        self.s("answer.no", "No")
        self.s("answer.unsure", "Not sure")
        for q in self.cur["questions"]["Q"]:
            qid = q["id"]
            if not qid.startswith("ask."):
                self.err.add(f"question {qid} must start with ask.")
            item = {"id": qid, "textKey": self.s(f"{qid}.text", q["text"])}
            if q["help"]:
                item["helpKey"] = self.s(f"{qid}.help", q["help"])
            if q["options"]:
                item["kind"] = "singleChoice"
                item["options"] = [{"id": oid, "textKey": self.s(f"{qid}.{oid}", txt), "sets": sets} for oid, sets, txt in q["options"]]
            else:
                item["kind"] = "yesNo"
                item["options"] = [{"id": "yes", "textKey": "answer.yes", "sets": "true"},
                                   {"id": "no", "textKey": "answer.no", "sets": "false"},
                                   {"id": "unsure", "textKey": "answer.unsure", "sets": "unknown"}]
            if q["intake"]:
                item["intake"] = True
            item["symptoms"] = q["symptoms"]
            if q["ask_if"]:
                item["askIf"] = [{"signal": s, "is": v} for s, v in q["ask_if"]]
            out.append(item)
            self.sig(qid, "ask", q["text"].rstrip("?").lower()[:120], question=qid)
            if q["options"]:
                for oid, _, txt in q["options"]:
                    self.sig(f"{qid}.{oid}", "ask", f"{q['text'].rstrip('?')}: {txt}", question=qid)
        self.question_ids = {q["id"] for q in out}
        self.files["questions/questions.json"] = out

    def build_builtin_signals(self):
        cs = self.cur["signals"]
        self.builtin_checks: dict[str, list[str]] = {}
        for sid, check, entity, admin, phrase in cs["BUILTIN"]:
            self.sig(sid, "builtin", phrase, entity=entity, admin=admin)
            self.builtin_checks.setdefault(check, []).append(sid)
        for sid, coll, minc, days, sat, feq, phrase in cs["COLLECTOR_SIGNALS"]:
            fc = drop_none({"collector": coll, "minCount": minc, "withinDays": days, "fieldEquals": feq or None, "saturateAt": sat})
            self.sig(sid, "collector", phrase, from_collector=fc)
        for sid, phrase in cs["MONITOR_SIGNALS"]:
            self.sig(sid, "monitor", phrase)
        for ev_key, text in cs.get("EVIDENCE", {}).items():
            self.s(ev_key, text)
        catalog_checks = {d["check_id"] for p in self.catalog for d in p["diagnostic_checks"]}
        missing = sorted(catalog_checks - set(self.builtin_checks))
        if missing:
            self.err.add(f"catalog check ids without a built-in signal: {missing}")

    def build_collectors(self):
        out = []
        for c in self.cur["collectors"]["COLLECTORS"]:
            item = {"kind": c["kind"], "id": c["id"], "descriptionKey": self.s(f"collector.{c['id']}.desc", c["desc"])}
            for k in ("log", "providers", "eventIds", "levels", "sinceDays", "max", "project"):
                if k in c:
                    item[k] = c[k]
            out.append(item)
        self.collectors = out

    # ---- conflicts: ConflictDef + signals + generated registry/file collectors + generated rules (later)
    def conflict_signal_suffixes(self, d) -> list[str]:
        sfx = []
        if d.get("uninstall_displayname_regex"):
            sfx.append("installed")
        if d.get("process_names"):
            sfx.append("running")
        if d.get("service_names"):
            sfx += ["service_present", "service_running"]
        if d.get("driver_names"):
            sfx.append("driver_loaded")
        if d.get("network_adapter_regex"):
            sfx += ["adapter_present", "adapter_enabled"]
        if d.get("module_names"):
            sfx.append("crash_module")
        if d.get("audio_endpoint_regex"):
            sfx.append("audio_endpoint")
        if d.get("windows_features"):
            sfx.append("feature_enabled")
        return sfx

    @staticmethod
    def reg_allowed(hive: str, key: str) -> bool:
        k = ("HKCU:" + key) if hive == "hkcuPlayer" else key
        kl = k.lower()
        if any(kl.startswith(d.lower()) for d in DENIED_REG):
            return False
        return any(kl.startswith(r.lower()) for r in ALLOWED_REG_ROOTS)

    def build_conflicts(self):
        cc = self.cur["conflicts"]
        by_kind: dict[str, list] = {}
        self.conflict_meta: dict[str, dict] = {}
        for e in self.conflicts_src:
            cid, kind, d = e["id"], e["kind"], e["detect"]
            ov = cc["OVERRIDES"].get(cid, {})
            name = e["name"]
            cat = ov.get("category", cc["KIND_CATEGORY"][kind])
            symptoms = ov.get("symptoms") or self.symptoms_for_category(cat)
            effect_keys = [self.s(f"conflict.{cid}.effect.{i}", t) for i, t in enumerate(e.get("effects", []))]
            sfx = self.conflict_signal_suffixes(d)
            for sx in sfx:
                self.sig(f"conflict.{cid}.{sx}", "conflict", SUFFIX_PHRASE[sx].format(name=name), entity=SUFFIX_ENTITY[sx])
            # registry / file: one collector each (first allowed path), declared as collector-derived signals
            reg_coll = None
            for rk in d.get("registry_keys") or []:
                hive_s, _, path = rk.partition("\\")
                hive = {"HKLM": "hklm", "HKCU": "hkcuPlayer"}.get(hive_s.upper())
                if not hive:
                    continue
                parent, _, last = path.rpartition("\\")
                if last in cc["REGISTRY_KEYS_NOT_VALUES"]:
                    key, values = path, []
                else:
                    key, values = parent, [last]
                if not self.reg_allowed(hive, key):
                    continue
                reg_coll = {"kind": "registryValue", "id": f"conflict_{cid}_reg", "hive": hive, "key": key, "values": values,
                            "descriptionKey": self.s(f"collector.conflict_{cid}_reg.desc", f"Registry setting used by {name}")}
                feq = {"data": cc["REGISTRY_DATA_EQUALS"][last]} if values and last in cc["REGISTRY_DATA_EQUALS"] else None
                self.collectors.append(reg_coll)
                self.sig(f"conflict.{cid}.registry_present", "collector", SUFFIX_PHRASE["registry_present"].format(name=name),
                         entity="registryValue", from_collector=drop_none({"collector": reg_coll["id"], "minCount": 1, "fieldEquals": feq}))
                sfx.append("registry_present")
                break
            for fp in d.get("file_paths") or []:
                m = re.match(r"^[A-Za-z]:\\Windows\\(.+)$", fp)
                if not m:
                    continue
                fcoll = {"kind": "fileExists", "id": f"conflict_{cid}_file", "path": "%SystemRoot%\\" + m.group(1),
                         "descriptionKey": self.s(f"collector.conflict_{cid}_file.desc", f"Files left by {name}")}
                self.collectors.append(fcoll)
                self.sig(f"conflict.{cid}.file_present", "collector", SUFFIX_PHRASE["file_present"].format(name=name),
                         entity="file", from_collector={"collector": fcoll["id"], "minCount": 1, "fieldEquals": {"exists": "true"}})
                sfx.append("file_present")
                break
            self.sig(f"conflict.{cid}.any", "conflict", SUFFIX_PHRASE["any"].format(name=name))
            fix_ids = ov.get("fixes")
            if fix_ids is None:
                fix_ids = []
                if cid not in cc["NO_AUTOFIX"]:
                    # Never auto-stop VPNs, network boosters or anti-cheats: kill switches and filter drivers can cut
                    # the only connection or break other games. Those stay guide-only (uninstall / turn off in the app).
                    if d.get("service_names") and kind in ("audio", "rgb_vendor", "overlay", "monitoring", "other"):
                        fix_ids.append("fix.svc.disable_conflict_service")
                    if d.get("process_names") and kind in ("overlay", "monitoring", "rgb_vendor", "other", "tweak_tool"):
                        fix_ids.append("fix.generic.close_process")
            guide_ids = ov.get("guides")
            if guide_ids is None:
                guide_ids = list(cc["KIND_GUIDES"].get(kind, []))
            item = drop_none({
                "id": cid, "name": name, "kind": kind,
                "detect": {
                    "processNames": d.get("process_names") or [], "serviceNames": d.get("service_names") or [],
                    "driverNames": d.get("driver_names") or [], "moduleNames": d.get("module_names") or [],
                    "uninstallDisplayNameRegex": d.get("uninstall_displayname_regex"),
                    "networkAdapterRegex": d.get("network_adapter_regex"), "audioEndpointRegex": d.get("audio_endpoint_regex"),
                    "registryKeys": d.get("registry_keys") or [], "filePaths": d.get("file_paths") or [],
                    "windowsFeatures": d.get("windows_features") or [],
                },
                "effectKeys": effect_keys,
                "fix": {"actionKey": self.s(f"conflict.{cid}.fix", e["fix"]["action"]), "reversible": bool(e["fix"]["reversible"]),
                        "risk": e["fix"]["risk"], "fixIds": fix_ids, "guideIds": guide_ids},
                "confidence": e["confidence"], "category": cat, "symptoms": symptoms,
                "wave": ov.get("wave"),
                "evidence": {"redditMentions": e["evidence"].get("reddit_mentions", 0),
                             "solvedMentions": e["evidence"].get("reddit_solved_mentions", 0),
                             "exampleThreadIds": e["evidence"].get("example_thread_ids", []),
                             "webSources": e["evidence"].get("web_sources", [])},
            })
            by_kind.setdefault(kind, []).append(item)
            self.conflict_meta[cid] = dict(name=name, kind=kind, category=cat, symptoms=symptoms, suffixes=sfx, fixes=fix_ids,
                                           guides=guide_ids, evidence=ov.get("evidence"), wave=ov.get("wave"),
                                           solved=e["evidence"].get("reddit_solved_mentions", 0),
                                           threads=e["evidence"].get("example_thread_ids", []),
                                           urls=e["evidence"].get("web_sources", []), confidence=e["confidence"])
        for kind, items in sorted(by_kind.items()):
            self.files[f"conflicts/{kind}.json"] = items
        self.files["collectors/collectors.json"] = self.collectors

    def symptoms_for_category(self, cat: str) -> list[str]:
        return [sid for sid, cats in self.symptom_cats.items() if cat in cats][:4]

    # ---- fixes
    def build_fixes(self):
        fx = self.cur["fixes"]
        for k, t in {**fx["OUTCOMES"], **fx["DRIFTS"], **fx["GUARD_TEXTS"]}.items():
            self.s(k, t)
        cat_by_id = {p["id"]: p for p in self.catalog}
        for f in fx["FIXES"]:
            fid = f["id"]
            if fid in self.fix_ids:
                self.err.add(f"fix {fid} defined twice")
            self.fix_ids.add(fid)
            area = fid.split(".")[1]
            item = {
                "id": fid,
                "titleKey": self.s(f"{fid}.title", f["title"]),
                "explainKey": self.s(f"{fid}.explain", f["explain"]),
                "undoExplainKey": self.s(f"{fid}.undo", f["undo"]),
            }
            if f["notice"]:
                item["noticeKey"] = self.s(f"{fid}.notice", f["notice"])
            item["sideEffectKeys"] = [self.s(f"{fid}.side.{i}", t) for i, t in enumerate(f["side_effects"])]
            manual = self.cur["manual_steps"]["MANUAL"].get(fid)
            if not manual or not manual.get("en") or len(manual["en"]) != len(manual.get("tr", [])):
                self.err.add(f"fix {fid} needs manual steps with as many Turkish as English lines")
            else:
                item["manualStepKeys"] = [self.s(f"{fid}.manual.{i}", (en, tr)) for i, (en, tr) in enumerate(zip(manual["en"], manual["tr"]))]
            item.update({"risk": f["risk"], "reversibility": f["rev"], "admin": f["admin"], "reboot": f["reboot"],
                         "scope": f["scope"], "estMinutes": f["minutes"], "appliesTo": f["applies"]})
            if f["template"]:
                item["template"] = True
            item["bind"] = {name: drop_none({"kind": kind, "fromSignal": src, "pick": pick}) for name, (kind, src, pick) in f["bind"].items()}
            item["guards"] = f["guards"]
            item["steps"] = f["steps"]
            item["verify"] = f["verifies"]
            if f["deadman"]:
                item["deadman"] = {"seconds": 60, "check": "connectivity.internet"}
            item["outcome"] = drop_none({"kind": f["outcome_kind"], "askKey": f["outcome_key"], "monitorSignals": f["monitor_signals"] or None})
            if f["drift_key"]:
                item["drift"] = {"watch": True, "reasonKey": f["drift_key"]}
            item["successPrior"] = self.success_prior(f, cat_by_id)
            item["minApp"] = "0.2.0"
            item["tested"] = f["tested"]
            self.files[f"fixes/{area}/{fid}.json"] = item

    @staticmethod
    def success_prior(f, cat_by_id) -> dict:
        success = trials = 0
        pats = [re.compile(p, re.I) for p in f["success_match"]]
        for hid in f["applies"]:
            p = cat_by_id.get(hid)
            if not p:
                continue
            for fx in p["fixes"]:
                trials += fx["count"]
                if pats and any(r.search(fx["fix"]) for r in pats) and fx["evidence"] in ("op_confirmed", "guide", "suggested"):
                    success += fx["count"] if fx["evidence"] != "suggested" else max(1, fx["count"] // 2)
        if trials == 0 or success == 0:
            return {"success": 1, "trials": 3}
        return {"success": min(success, trials), "trials": trials}

    # ---- guides and tables
    def build_guides(self):
        for t in self.tables_src:
            item = {"id": t["id"], "keyColumn": t.get("key_column"), "rows": t["rows"]}
            self.files[f"tables/{t['id']}.json"] = drop_none(item)
        self.table_ids = {t["id"] for t in self.tables_src}
        for g in self.guides_src:
            gid = g["id"]
            if gid in self.guide_ids:
                self.err.add(f"guide {gid} defined twice")
            self.guide_ids.add(gid)
            item = {"id": gid, "titleKey": self.s(f"{gid}.title", g["title"]), "summaryKey": self.s(f"{gid}.summary", g["summary"]),
                    "bucket": g.get("bucket", "guidedManual"), "risk": g.get("risk", "high"), "reboot": g.get("reboot", "none"),
                    "appliesTo": g.get("applies", [])}
            pre = []
            for i, p in enumerate(g.get("preflight") or []):
                pre.append({"probe": p["probe"], "must": p.get("must", "true"), "severity": p.get("severity", "block"),
                            "failKey": self.s(f"{gid}.pre.{i}", p["fail"])})
            item["preflight"] = pre
            branches = []
            for b in g["branches"]:
                bid = b["id"]
                br = {"id": bid, "when": [{"signal": s, "is": v} for s, v in b.get("when", [])]}
                if b.get("text"):
                    br["textKey"] = self.s(f"{gid}.{bid}.text", b["text"])
                steps = []
                for i, st in enumerate(b["steps"]):
                    step = {"textKey": self.s(f"{gid}.{bid}.s{i + 1}", st["text"]), "action": st.get("action") or "none"}
                    for src, dst in (("target", "target"), ("vendor_table", "vendorTable"), ("anchor", "docsAnchor")):
                        if st.get(src):
                            step[dst] = st[src]
                    steps.append(step)
                br["steps"] = steps
                br["verify"] = [{"signal": s, "is": v} for s, v in b.get("verify", [])]
                if b.get("next_on_fail"):
                    br["nextOnFail"] = b["next_on_fail"]
                branches.append(br)
            item["branches"] = branches
            if g.get("docs"):
                item["docsPath"] = g["docs"]
            if g.get("outcome"):
                item["outcomeAskKey"] = self.s(f"{gid}.outcome", g["outcome"])
            self.files[f"guides/{gid}.json"] = item

    # ---- rules
    def build_rules(self):
        curated = self.cur["rules"]["RULES"]
        cat_ids = {p["id"] for p in self.catalog}
        missing = sorted(cat_ids - set(curated))
        if missing:
            self.err.add(f"catalog problems without a curated rule: {missing}")
        for p in self.catalog:
            r = curated.get(p["id"])
            if r:
                self.emit_rule(r, category=p["category"], threads=p["thread_count"], sources=self.catalog_sources(p))
        for rid, r in curated.items():
            if rid in cat_ids:
                continue
            if r["kind"] != "route":
                self.err.add(f"curated rule {rid} is not in the catalog and is not a route")
            self.emit_rule(r, category="other", threads=0, sources=None)
        # generated, one per conflict entry
        superseded = {s for r in curated.values() for s in r["supersedes"]}
        weights = self.cur["conflicts"]["DEFAULT_WEIGHTS"]
        gen_by_cat: dict[str, list] = {}
        for cid, m in self.conflict_meta.items():
            rid = f"conflict-{cid}"
            ev = []
            if m["evidence"] is not None:
                for i, (sig, w) in enumerate(m["evidence"]):
                    ev.append({"id": f"e{i}", "signal": sig, "true": w})
            else:
                for sx in m["suffixes"]:
                    ev.append({"id": sx, "signal": f"conflict.{cid}.{sx}", "true": weights[sx],
                               "group": "presence" if sx in ("installed", "service_present", "registry_present", "file_present", "adapter_present") else None})
                ev = [drop_none(e) for e in ev]
            title = (f"{m['name']} may be interfering with VALORANT", f"{m['name']} VALORANT ile çakışıyor olabilir")
            summary = (f"VTDiag found {m['name']} on this PC. Players have reported problems with it in VALORANT; the details below say what to try.",
                       f"VTDiag bu bilgisayarda {m['name']} buldu. Oyuncular VALORANT'ta bununla sorun yaşadıklarını bildirdi; ne deneyeceğin aşağıda yazıyor.")
            rule = drop_none({
                "id": rid, "rev": 1, "kind": "generated", "category": m["category"],
                "titleKey": self.s(f"rule.{rid}.title", title), "summaryKey": self.s(f"rule.{rid}.summary", summary),
                "bucket": "fixableHere" if m["fixes"] else "guidedManual",
                "symptoms": m["symptoms"], "prior": {"threads": 0},
                "requires": [] if m["evidence"] is not None else [f"conflict.{cid}.any"],
                "evidence": ev, "wave": m["wave"], "fixes": m["fixes"], "guides": m["guides"],
                "sources": {"catalog": None, "threadCount": m["solved"], "evidence": "conflict_registry",
                            "threadIds": m["threads"][:5], "urls": m["urls"]},
            })
            if rid in superseded:
                rule["related"] = sorted(r["id"] for r in curated.values() if rid in r["supersedes"])
            self.rules[rid] = rule
            gen_by_cat.setdefault(m["category"], []).append(rule)
        for cat, items in sorted(gen_by_cat.items()):
            self.files[f"rules/generated/{cat}.json"] = items

    def catalog_sources(self, p) -> dict:
        order = {"op_confirmed": 0, "guide": 1, "commenter_suggested": 2, "suggested": 3}
        best = min((f["evidence"] for f in p["fixes"]), key=lambda e: order.get(e, 9), default=None)
        urls = []
        for c in self.codes_src["error_codes"]:
            if set(parse_code_row(c["code"])) & {normalize_code(x) for x in p["error_codes"]}:
                urls += [u for u in c.get("source_urls", []) if u not in urls]
        return drop_none({"catalog": p["id"], "threadCount": p["thread_count"], "evidence": best,
                          "threadIds": p["thread_ids"][:5], "urls": urls[:4]})

    def emit_rule(self, r, *, category, threads, sources):
        rid = r["id"]
        ev = []
        for e in r["ev"]:
            item = {"id": e["id"], "signal": e["signal"], "true": e["t"], "false": e["f"], "veto": e["veto"], "group": e["group"],
                    "ask": e["ask"]}
            if e["why"]:
                item["textKey"] = self.s(f"rule.{rid}.ev.{e['id']}", e["why"])
            if e["only_if"]:
                item["onlyIfSymptoms"] = e["only_if"]
            ev.append(drop_none(item))
        prior = {"threads": threads}
        if r["prior_override"] is not None:
            prior.update({"override": r["prior_override"], "overrideReason": r["override_reason"]})
        rule = drop_none({
            "id": rid, "rev": 1, "kind": r["kind"], "category": category,
            "titleKey": self.s(f"rule.{rid}.title", r["title"]), "summaryKey": self.s(f"rule.{rid}.summary", r["summary"]),
            "bucket": r["bucket"], "route": r["route"], "symptoms": r["symptoms"], "prior": prior,
            "requires": r["requires"], "excludes": r["excludes"], "evidence": ev,
            "wave": r["wave"], "fixes": r["fixes"], "guides": r["guides"], "questions": r["questions"],
            "supersedes": r["supersedes"], "related": r["related"], "sources": sources,
        })
        self.rules[rid] = rule
        folder = "routes" if r["kind"] == "route" else category
        self.files[f"rules/{folder}/{rid}.json"] = rule

    # ---- codes
    def build_codes(self):
        cd = self.cur["codes"]
        rows = []
        seen: dict[str, str] = {}
        src_by_code = {c["code"]: c for c in self.codes_src["error_codes"]}
        for raw, c in src_by_code.items():
            if raw not in cd["ROWS"]:
                self.err.add(f"error code row not curated: {raw}")
        for raw, cur in cd["ROWS"].items():
            src = src_by_code.get(raw)
            if not src:
                self.err.add(f"curated code row not in research/error_codes.json: {raw}")
                continue
            codes = parse_code_row(raw)
            split = cur.get("split", {})
            codes = [c for c in codes if c not in split]
            codes += [normalize_code(a) for a in cur.get("aliases", [])]
            rid = code_slug(codes[0]) + ("-group" if len(parse_code_row(raw)) > 1 else "")
            meaning_key = self.s(f"code.{rid}.meaning", src["official_meaning"])
            rows.append(self.code_row(rid, codes, src["category"], meaning_key, cur, src.get("source_urls", []), src.get("confidence", "medium")))
            for sc, sid in split.items():
                sp = cd["SPLIT_ROWS"][sid]
                rows.append(self.code_row(sid, [normalize_code(x) for x in sp["codes"]], src["category"],
                                          self.s(f"code.{sid}.meaning", src["official_meaning"]), sp, src.get("source_urls", []),
                                          src.get("confidence", "medium")))
        for rid, ex in cd["EXTRA_ROWS"].items():
            rows.append(self.code_row(rid, [normalize_code(x) for x in ex["codes"]], ex["category"],
                                      self.s(f"code.{rid}.meaning", ex["meaning"]), ex, [], "low"))
        for row in rows:
            for c in row["codes"]:
                if c in seen:
                    self.err.add(f"code {c} in two rows: {seen[c]} and {row['id']}")
                seen[c] = row["id"]
            self.sig(f"code.{row['id']}", "code", f"error code {', '.join(row['codes'][:3])}")
        for k, t in cd["ROUTE_TEXT"].items():
            self.s(f"route.{k}.text", t)
        self.code_rows = rows
        self.files["codes/codes.json"] = rows

    @staticmethod
    def code_row(rid, codes, category, meaning_key, cur, urls, confidence):
        return drop_none({
            "id": rid, "codes": codes, "category": category, "meaningKey": meaning_key, "route": cur.get("route"),
            "pointsTo": [{"hypothesis": h, "logged": lg, "typed": ty} for h, (lg, ty) in cur.get("points", [])],
            "officialUrls": [u for u in urls if u.startswith("https://support.riotgames.com") or u.startswith("https://riotgames.com")],
            "askIfTyped": cur.get("ask_if_typed"), "confidence": confidence,
        })

    def build_ui_strings(self):
        for k, t in self.cur["ui_strings"]["STRINGS"].items():
            self.s(k, t)
        for k, t in self.cur["evidence_strings"]["EVIDENCE"].items():
            self.s(k, t)

    # ---- signals file (after everything declared)
    def finish_signals(self):
        by_source: dict[str, list] = {}
        for s in self.signals.values():
            by_source.setdefault(s["source"], []).append(s)
        for src, items in sorted(by_source.items()):
            self.files[f"signals/{src}.json"] = sorted(items, key=lambda x: x["id"])

    # ---- lint (Python side; the full lint runs in C# tests/VTD.Kb.Tests)
    def sig_ok(self, sid: str) -> bool:
        if sid in self.signals:
            return True
        if "*" in sid:
            rx = re.compile("^" + re.escape(sid).replace("\\*", "[^.]+") + "$")
            return any(rx.match(s) for s in self.signals)
        return False

    def lint(self):
        rule_ids = set(self.rules)
        for rid, r in self.rules.items():
            for s in r.get("requires", []) + r.get("excludes", []):
                if not self.sig_ok(s):
                    self.err.add(f"rule {rid}: unknown signal {s}")
            for e in r.get("evidence", []):
                if not self.sig_ok(e["signal"]):
                    self.err.add(f"rule {rid}: unknown signal {e['signal']}")
                if e.get("ask") and e["ask"] not in self.question_ids:
                    self.err.add(f"rule {rid}: unknown question {e['ask']}")
                for sy in e.get("onlyIfSymptoms", []):
                    if sy not in self.symptom_ids:
                        self.err.add(f"rule {rid}: unknown symptom {sy}")
            for sy in r.get("symptoms", []):
                if sy not in self.symptom_ids:
                    self.err.add(f"rule {rid}: unknown symptom {sy}")
            for f in r.get("fixes", []):
                if f not in self.fix_ids:
                    self.err.add(f"rule {rid}: unknown fix {f}")
            for g in r.get("guides", []):
                if g not in self.guide_ids:
                    self.err.add(f"rule {rid}: unknown guide {g}")
            for q in r.get("questions", []):
                if q not in self.question_ids:
                    self.err.add(f"rule {rid}: unknown question {q}")
            for x in r.get("supersedes", []) + r.get("related", []):
                if x not in rule_ids:
                    self.err.add(f"rule {rid}: unknown rule {x}")
        for row in self.code_rows:
            for p in row.get("pointsTo", []):
                if p["hypothesis"] not in rule_ids:
                    self.err.add(f"code {row['id']}: unknown hypothesis {p['hypothesis']}")
                if p["typed"] > p["logged"]:
                    self.err.add(f"code {row['id']}: typed > logged")
            if row.get("askIfTyped") and row["askIfTyped"] not in self.question_ids:
                self.err.add(f"code {row['id']}: unknown question {row['askIfTyped']}")
        for path, obj in self.files.items():
            if path.startswith("fixes/"):
                self.lint_fix(obj, rule_ids)
            elif path.startswith("guides/"):
                self.lint_guide(obj, rule_ids)
            elif path.startswith("conflicts/"):
                for c in obj:
                    for f in c["fix"]["fixIds"]:
                        if f not in self.fix_ids:
                            self.err.add(f"conflict {c['id']}: unknown fix {f}")
                    for g in c["fix"]["guideIds"]:
                        if g not in self.guide_ids:
                            self.err.add(f"conflict {c['id']}: unknown guide {g}")

    def lint_fix(self, f, rule_ids):
        fid = f["id"]
        for h in f["appliesTo"]:
            if h not in rule_ids:
                self.err.add(f"fix {fid}: unknown hypothesis {h}")
        for name, b in f["bind"].items():
            if b.get("fromSignal") and not self.sig_ok(b["fromSignal"]):
                self.err.add(f"fix {fid}: bind {name} from unknown signal {b['fromSignal']}")
        for g in f["guards"]:
            if g["probe"] not in GUARD_IDS and g["probe"] not in self.signals:
                self.err.add(f"fix {fid}: unknown guard probe {g['probe']}")
        net = False
        for st in f["steps"]:
            op = self.ops.get(st["op"])
            if not op:
                self.err.add(f"fix {fid}: unknown op {st['op']}")
                continue
            net |= op["affectsNetwork"] or (st["op"] == "device.disable" and any(b["kind"] == "adapter" for b in f["bind"].values()))
            self.lint_args(fid, op, st["args"])
            for v in st["args"].values():
                for ref in re.findall(r"\$([a-z]+)\.(id|name)", v):
                    if ref[0] not in f["bind"]:
                        self.err.add(f"fix {fid}: ${ref[0]} is not bound")
        for v in f["verify"]:
            op = self.ops.get(v["probe"])
            if not op:
                if v["probe"] not in GUARD_IDS:
                    self.err.add(f"fix {fid}: unknown verify probe {v['probe']}")
                continue
            for k in v["expect"]:
                if k not in op["stateKeys"]:
                    self.err.add(f"fix {fid}: verify key {k} not in {v['probe']} stateKeys")
        if net and "deadman" not in f:
            self.err.add(f"fix {fid}: network-affecting fix needs a deadman")
        if f["risk"] == "high" and f["steps"]:
            self.err.add(f"fix {fid}: high-risk fixes are guides only")

    def lint_args(self, fid, op, args):
        names = {a["name"]: a for a in op["args"]}
        for k, v in args.items():
            a = names.get(k)
            if not a:
                self.err.add(f"fix {fid}: {op['op']} has no arg {k}")
                continue
            if v.startswith("$"):
                continue
            if a["allowedValues"] and v not in a["allowedValues"]:
                self.err.add(f"fix {fid}: {op['op']}.{k}={v} not allowed")
            if a.get("pattern") and not re.match(a["pattern"], v):
                self.err.add(f"fix {fid}: {op['op']}.{k}={v} fails pattern")
        for a in op["args"]:
            if a["required"] and a["name"] not in args:
                self.err.add(f"fix {fid}: {op['op']} missing required arg {a['name']}")

    def lint_guide(self, g, rule_ids):
        gid = g["id"]
        for h in g["appliesTo"]:
            if h not in rule_ids:
                self.err.add(f"guide {gid}: unknown hypothesis {h}")
        for p in g["preflight"]:
            if p["probe"] not in GUARD_IDS and p["probe"] not in self.signals:
                self.err.add(f"guide {gid}: unknown preflight probe {p['probe']}")
        bids = {b["id"] for b in g["branches"]}
        if not any(not b["when"] for b in g["branches"]):
            self.err.add(f"guide {gid}: no default branch")
        for b in g["branches"]:
            for c in b["when"] + b["verify"]:
                if c["signal"] not in self.signals:
                    self.err.add(f"guide {gid}/{b['id']}: unknown signal {c['signal']}")
            if b.get("nextOnFail") and b["nextOnFail"] not in bids:
                self.err.add(f"guide {gid}/{b['id']}: unknown nextOnFail {b['nextOnFail']}")
            for st in b["steps"]:
                act, tgt = st["action"], st.get("target")
                if act == "runFix" and tgt not in self.fix_ids:
                    self.err.add(f"guide {gid}: unknown fix {tgt}")
                if act == "runCheck" and tgt not in self.signals and tgt not in GUARD_IDS:
                    self.err.add(f"guide {gid}: unknown check {tgt}")
                if act == "openSettings" and not (tgt or "").startswith("ms-settings:"):
                    self.err.add(f"guide {gid}: bad settings target {tgt}")
                if act == "copyText" and not tgt:
                    self.err.add(f"guide {gid}: copyText without text")
                if st.get("vendorTable") and st["vendorTable"] not in self.table_ids:
                    self.err.add(f"guide {gid}: unknown table {st['vendorTable']}")


# ------------------------------------------------------------------------------------------------------------ output
def render(builder: Builder) -> dict[str, bytes]:
    builder.finish_signals()
    out: dict[str, bytes] = {}
    for rel, obj in sorted(builder.files.items()):
        out[rel] = dumps(obj).encode("utf-8")
    rel_ = builder.release
    manifest = {
        "format": 1, "version": rel_["version"], "minApp": rel_["minApp"], "createdUtc": rel_["createdUtc"],
        "channel": rel_.get("channel", "stable"),
        "files": [{"path": rel, "sha256": hashlib.sha256(data).hexdigest(), "size": len(data)} for rel, data in sorted(out.items())],
    }
    out["kb.manifest.json"] = dumps(manifest).encode("utf-8")
    revocations = {"serial": rel_.get("revocationSerial", 1), "issuedUtc": rel_["createdUtc"],
                   "revokedVersions": rel_.get("revokedVersions", []), "revokedKeyIds": rel_.get("revokedKeyIds", [])}
    out["kb.revocations.json"] = dumps(revocations).encode("utf-8")
    return out


def managed_files() -> list[Path]:
    keep = []
    for p in KB.rglob("*"):
        if p.is_file() and "schema" not in p.relative_to(KB).parts and not p.name.endswith(".sig"):
            keep.append(p)
    return keep


DOCS = ROOT / "docs"
MANUAL_DOCS = {
    "en": ("MANUAL_FIXES.md", "Fix it yourself", "Every fix VTDiag can propose, done by hand. VTDiag v0.1 only scans; use these steps for the fixes your report lists. "
           "Change one thing at a time and play a match before the next one.", "Undo", "Admin rights needed", "Restart needed",
           "This file is generated by `tools/kb_builder.py` from `tools/kb_curation/manual_steps.py`. Do not edit it by hand."),
    "tr": ("MANUAL_FIXES.tr.md", "Kendin düzelt", "VTDiag'ın önerebileceği her düzeltmenin elle yapılışı. VTDiag v0.1 sadece tarama yapar; raporunda geçen düzeltmeler için bu adımları kullan. "
           "Her seferinde tek bir şeyi değiştir ve sıradakine geçmeden önce bir maç oyna.", "Geri alma", "Yönetici izni gerekir", "Yeniden başlatma gerekir",
           "Bu dosya `tools/kb_builder.py` tarafından `tools/kb_curation/manual_steps.py` dosyasından üretilir. Elle düzenleme."),
}


def render_docs(b: Builder) -> dict[Path, bytes]:
    tr_all = {**b.tr_src, **b.tr}
    fixes = sorted((v for k, v in b.files.items() if k.startswith("fixes/")), key=lambda f: f["id"])
    out = {}
    for lang, (name, title, intro, undo, admin, reboot, note) in MANUAL_DOCS.items():
        def t(key):
            return tr_all.get(key, b.en[key]) if lang == "tr" else b.en[key]
        lines = [f"# {title}", "", intro, "", f"<!-- {note} -->", ""]
        for f in fixes:
            lines += [f"## {t(f['titleKey'])}", "", f"`{f['id']}`", "", t(f["explainKey"]), ""]
            tags = [x for x, on in ((admin, f["admin"]), (reboot, f["reboot"] != "none")) if on]
            if tags:
                lines += [" · ".join(f"**{x}**" for x in tags), ""]
            lines += [f"{i}. {t(k)}" for i, k in enumerate(f.get("manualStepKeys", []), 1)]
            lines += ["", f"**{undo}:** {t(f['undoExplainKey'])}", ""]
        out[DOCS / name] = ("\n".join(lines).rstrip() + "\n").encode("utf-8")
    return out


def cmd_build(args) -> int:
    b = Builder().build()
    if b.err:
        for e in b.err:
            print("ERROR:", e, file=sys.stderr)
        print(f"{len(b.err)} error(s); kb/ not written.", file=sys.stderr)
        return 1
    out = render(b)
    for p in managed_files():
        rel = p.relative_to(KB).as_posix()
        if rel not in out:
            p.unlink()
    for rel, data in out.items():
        dst = KB / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        if not dst.exists() or dst.read_bytes() != data:
            dst.write_bytes(data)
    for sig in (KB / "kb.manifest.sig", KB / "kb.revocations.sig"):
        if sig.exists():
            sig.unlink()  # contents changed: the pack must be re-signed
    for dst, data in render_docs(b).items():
        if not dst.exists() or dst.read_bytes() != data:
            dst.write_bytes(data)
    stats(b)
    return 0


def cmd_check(args) -> int:
    b = Builder().build()
    if b.err:
        for e in b.err:
            print("ERROR:", e, file=sys.stderr)
        return 1
    out = render(b)
    diff = [rel for rel, data in out.items() if not (KB / rel).exists() or (KB / rel).read_bytes() != data]
    extra = [p.relative_to(KB).as_posix() for p in managed_files() if p.relative_to(KB).as_posix() not in out]
    diff += [p.relative_to(ROOT).as_posix() for p, data in render_docs(b).items() if not p.exists() or p.read_bytes() != data]
    if diff or extra:
        print("kb/ is out of date. Run: python -I tools/kb_builder.py build", file=sys.stderr)
        for d in diff + extra:
            print("  ", d, file=sys.stderr)
        return 1
    print("kb/ is up to date.")
    return 0


def stats(b: Builder):
    rules = list(b.rules.values())
    kinds = {}
    for r in rules:
        kinds[r["kind"]] = kinds.get(r["kind"], 0) + 1
    en, tr = len(b.files["strings/en.json"]), len(b.files["strings/tr.json"])
    print(f"rules {kinds} | conflicts {len(b.conflicts_src)} | code rows {len(b.code_rows)} | fixes {len(b.fix_ids)} | "
          f"guides {len(b.guide_ids)} | questions {len(b.question_ids)} | signals {len(b.signals)} | "
          f"collectors {len(b.collectors)} | strings en {en} tr {tr} ({tr * 100 // max(en, 1)}%)")


def cmd_keygen(args) -> int:
    out = Path(args.out)
    if out.exists():
        print(f"{out} exists; refusing to overwrite a key.", file=sys.stderr)
        return 1
    seed = os.urandom(32)
    pub = ed25519_public(seed)
    out.write_text(base64.b64encode(seed).decode("ascii") + "\n", encoding="ascii")
    print("private seed written to", out, "(never commit it)")
    print("public key: ed25519:" + base64.b64encode(pub).decode("ascii"))
    print("key id:    ", key_id(pub))
    return 0


def cmd_sign(args) -> int:
    seed = base64.b64decode(Path(args.key).read_text(encoding="ascii").strip())
    pub = ed25519_public(seed)
    for name in ("kb.manifest.json", "kb.revocations.json"):
        data = (KB / name).read_bytes()
        sig = {"alg": "ed25519", "keyId": key_id(pub), "sig": base64.b64encode(ed25519_sign(seed, data)).decode("ascii")}
        (KB / name.replace(".json", ".sig")).write_text(dumps(sig), encoding="utf-8")
    print("signed with", key_id(pub))
    return 0


def cmd_tr_missing(args) -> int:
    b = Builder().build()
    en, tr = b.files["strings/en.json"], b.files["strings/tr.json"]
    missing = {k: v for k, v in en.items() if k not in tr}
    Path(args.out).write_text(dumps(missing), encoding="utf-8")
    print(f"{len(missing)} strings without Turkish written to {args.out}")
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd")
    sub.add_parser("build")
    sub.add_parser("check")
    k = sub.add_parser("keygen")
    k.add_argument("--out", required=True)
    s = sub.add_parser("sign")
    s.add_argument("--key", required=True)
    t = sub.add_parser("tr-missing")
    t.add_argument("--out", required=True)
    args = ap.parse_args(argv)
    return {"build": cmd_build, "check": cmd_check, "keygen": cmd_keygen, "sign": cmd_sign, "tr-missing": cmd_tr_missing,
            None: cmd_build}[args.cmd](args)


if __name__ == "__main__":
    sys.exit(main())
