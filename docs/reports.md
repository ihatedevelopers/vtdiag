# VTDiag reports

The HTML, Markdown and JSON reports and the "Copy for AI" digest are all built from one redacted model (`ReportModel`,
schema [`../kb/schema/report.schema.json`](../kb/schema/report.schema.json)). Every export is share-level redacted and
swept first; see [privacy.md](privacy.md).

Every report says which **bucket** the player is in, in plain words, and never claims VTDiag can fix what a PC tool
cannot:

- Fixable here;
- Guided manual step;
- ISP or network outside this PC;
- Riot or account side;
- Likely hardware;
- Unknown.

Every report also says "VTDiag is not affiliated with Riot Games".

## 1. HTML report: `VTDiag-Report-<yyyymmdd>-<shortid>.html`

A single file with inline CSS and an inline SVG chart. It opens offline, prints cleanly, and has dark text on a light
background.

**No JavaScript, ever.**

- No `<script>`, no `on…=` event attributes and no links at all.
- A Content-Security-Policy (`default-src 'none'; style-src 'unsafe-inline'; img-src data:`) stops anything else from
  loading.
- Every value is HTML-escaped. `=`, `:` and backticks are written as entities too, so text such as `onerror=` or
  `javascript:` from a log can't even look like markup.

Sections:

- **Summary:** the bucket sentence and the top suspect.
- **Health checklist:** OK, Worth a look, Problem or "Couldn't check: <reason>". Unknown is never shown as OK.
- **Likely causes:** confidence words (Very likely, Likely, Possible, Long shot), never percentages, and the top three
  "why" lines.
- **What we couldn't check.**
- **What was tried:** status, outcome, whether it can be undone, restart needed, restore point.
- **Match monitor:** FPS, 1% low, stutters per minute and periodicity, ping, jitter, loss, the server region, and an
  FPS-per-minute chart.
- **This PC:** a specs one-liner and the Vanguard readiness line (TPM 2.0, Secure Boot, UEFI, GPT, Memory Integrity,
  virtualization, test signing).
- **Scan coverage:** collectors that did not fully run, and why.
- **For helpers:** posteriors, log-odds, per-signal contributions, fix proposals with their share-level bindings,
  codes, ruled-out and unexplained findings. This section is in the helper language (English by default).
- **Privacy:** what was redacted, and what you left out.
- **Footer:** app and KB version, report id, machine key, and the SHA-256 of the JSON report.

## 2. Markdown (Copy for Reddit/Discord): `VTDiag-Report-<yyyymmdd>-<shortid>.md`

**At most 9,000 characters, always** (Reddit comments stop at 10,000; the limit is `ReportOptions.MarkdownMaxChars`).

Sections are added in priority order:

1. verdict and bucket;
2. specs one-liner;
3. Vanguard readiness;
4. top three suspects with "why";
5. what was tried and the outcome;
6. monitor numbers;
7. could not check.

Long lists shrink to fewer items plus "…and N more" before a whole section is dropped. Single lines are capped. The
"Full report: attach the .html" line and the disclaimer are always kept. Values are Markdown-escaped, so a log line
can't create links or formatting.

## 3. JSON: `VTDiag-Report-<yyyymmdd>-<shortid>.json`

This is exactly `VtdJson.Serialize(reportModel)`: camelCase, string enums, strict. `ReportBundle.JsonSha256` is the
SHA-256 (lowercase hex) of exactly that UTF-8 text. The HTML footer prints the same hash, so a helper can check that
the HTML and JSON belong together and the JSON was not edited.

## 4. Copy for AI: `VTDiag-AI-<yyyymmdd>-<shortid>.md`

A compact digest for a helper's AI agent. It is always English and is swept like everything else. It contains:

- the values a plan needs (`forSnapshotId`, `kbMin`);
- specs, Vanguard readiness, and collectors that did not fully run;
- ranked hypotheses with ids, evidence signal ids and states, and entity ids;
- could-not-check items, codes, attempts and the monitored match;
- the **only** KB ids a plan may use:
  - fixes, with risk, reversibility, bind names and candidate share ids (with labels such as "carries the internet
    connection: never disable");
  - guides, with their branches;
  - questions;
- the plan rules, the full `plan.schema.json`, and an example plan that validates on this PC.

See [plans.md](plans.md).

## 5. Bundle: `VTDiag-<shortid>.vtdscan`

A zip with:

- `manifest.json` (format `vtdscan/1`, the SHA-256, size and content type of every part);
- `snapshot.redacted.json`;
- `analysis.json`;
- `journal.summary.json` (revision 2 bundles, after fixes);
- `monitor/<id>.json`;
- `report.html`.

The writer refuses anything that is not share-level redacted, or that the sweep flags. The reader treats the file as
hostile:

- at most 64 entries;
- 32 MB per entry and 64 MB in total;
- a compression-ratio guard;
- only known entry names (no traversal, no duplicates);
- the format major must match;
- every part is checked against its hash and size.

## Text and languages

The player-facing parts use the player's language (English or Turkish). KB texts come from the KB. Report headings,
bucket sentences, confidence words, status words and unknown reasons come from `ReportStrings(.tr).resx`. If a key has
no text anywhere, a readable fallback is shown instead of a raw dotted key. Numbers and dates use the language's
culture for display only; machine data stays invariant.
