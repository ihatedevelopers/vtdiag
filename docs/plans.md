# Helper plans (`.vtdplan`)

A helper (a person, or a helper's AI agent) reads a player's `.vtdscan` or "Copy for AI" digest and sends back a
**plan**. A plan is a short list of steps the player's VTDiag can run. The player approves each step one at a time.
The schema is [`../kb/schema/plan.schema.json`](../kb/schema/plan.schema.json).

## Format

```json
{
  "schema": 1,
  "planId": "01JB7Z6Q4N8V2X3Y5W7T9R1M0K",
  "forSnapshotId": "01JB6R7X3K9M2N4P5Q6R7S8T9V",
  "kbMin": "2026.10.21.1",
  "createdUtc": "2026-10-21T20:10:00Z",
  "expiresUtc": "2026-10-24T20:10:00Z",
  "author": { "name": "u/ModName" },
  "message": "Looks like your spare network port. Try step 1, then play a Deathmatch.",
  "steps": [
    {
      "kind": "fix",
      "fixId": "fix.net.disable_unused_adapter",
      "bind": { "adapter": ["PCI\\VEN_10EC&DEV_8125&SUBSYS_87D71043&REV_05\\h:8255a5a5e05ee9d5"] },
      "note": "You're on Wi-Fi, so this is safe. One click turns it back on."
    },
    { "kind": "monitor", "minutes": 10, "note": "Play one Deathmatch." },
    { "kind": "question", "questionId": "ask.only_valorant_affected" },
    { "kind": "guide", "guideId": "guide.secureboot.enable", "branchId": "uefi_sb_off" }
  ]
}
```

- `planId`: a new ULID (26 characters of Crockford base32).
- `forSnapshotId`: the `snapshotId` of the scan you were given.
- `kbMin`: the oldest knowledge-base version your plan needs. The player's KB must be this version or newer (CalVer,
  compared number by number).
- `steps`: `fix`, `guide`, `question` or `monitor`, at most 10.
- `bind`: for each binding name of the fix, the entity ids exactly as they appear in the share-level scan. The
  candidates are listed in the "Copy for AI" digest. Device ids end in a salted hash such as `h:8255…`; copy them as
  they are.

## Safety rules (the player's app enforces them)

**A plan is rejected outright when:**

| Rule | Issue code |
|---|---|
| The JSON is invalid, `schema` is not 1, a field is missing, an id is not a ULID, or a step `kind` is unknown | `schema` |
| The `message` is over 2,000 characters or a `note` is over 500 | `schema` |
| The current time is past `expiresUtc` | `expired` |
| `expiresUtc` is more than 7 days after `createdUtc` | `expired` |
| `createdUtc` is in the future (10-minute tolerance) | `expired` |
| The KB is older than `kbMin` | `kb_too_old` |
| There are more than 10 steps | `too_many_steps` |
| A signature is present but invalid | `bad_signature` |

**Only that step is greyed out, with a reason:**

- **An unknown id.** The fix, guide or question is not in the player's KB (`unknown_id`), or the guide branch does not
  exist.
- **A high-risk fix.** BIOS, Secure Boot and firmware changes are guides only.
- **A binding that does not resolve.** VTDiag rescans the PC. It computes the share id of every live entity of the
  bound kind; each id in your plan must match exactly one live entity (`plan.step.binding_unresolved`). Ids from
  another PC never match, because the hash salt is per install.
- **A missing or wrong binding.** A binding name is missing or unknown, or a "pick one" binding got several ids.
- **A protected target.** Plans can never bind:
  - Vanguard (`vgc`, `vgk`, `vgk.sys`, its files);
  - Riot or League processes;
  - the adapter that carries the internet connection;
  - the boot disk controller.
- **An entity the analysis didn't offer.** For a non-template fix, the entity must be one the player's fresh analysis
  offers for that fix.
- **Bad monitor minutes.** `minutes` must be from 1 to 60.

**Even a valid plan is not trusted:**

- Steps are only KB ids. There are no commands, scripts, file paths, registry keys or URLs as actions.
- Notes and the message are plain text. Links show as "link from helper" and are never opened automatically.
- Each step still goes through VTDiag's normal preview and consent screen. The engine re-checks guards and
  ProtectedTargetPolicy before anything changes. There is no "apply all".
- One-way fixes need a second click.

## Signing (optional)

A signed plan is an envelope around the exact plan bytes. No canonicalization is needed: sign the bytes you send.

```json
{
  "format": "vtdplan-signed/1",
  "payload": "<base64 of the UTF-8 plan JSON>",
  "publicKey": "ed25519:<base64 of the 32-byte public key>",
  "signature": "<base64 of the 64-byte Ed25519 signature over the decoded payload bytes>"
}
```

| Signature | What the player sees |
|---|---|
| Key in the KB's `trusted_helpers.json` | "Verified r/ValorantTechSupport helper" with your name |
| Any other valid key | Signed by an unknown key |
| No signature | "From someone you know? Only continue if you trust them." |
| Bad signature | The plan is rejected |

Signing raises trust but never removes consent. Any Ed25519 library works, for example with Python's `cryptography`:

```python
import base64, json
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
key = Ed25519PrivateKey.from_private_bytes(seed32)            # keep the seed secret
payload = open("plan.json", "rb").read()                       # the exact bytes
envelope = {
    "format": "vtdplan-signed/1",
    "payload": base64.b64encode(payload).decode(),
    "publicKey": "ed25519:" + base64.b64encode(key.public_key().public_bytes_raw()).decode(),
    "signature": base64.b64encode(key.sign(payload)).decode(),
}
open("VTDiag-1234ABCD.vtdplan", "w").write(json.dumps(envelope))
```

## For AI agents: writing a plan from a "Copy for AI" digest

The digest is everything you need. It has:

- the values to copy (`schema`, `forSnapshotId`, `kbMin`);
- the ranked hypotheses with their evidence signals and entity ids;
- what could not be checked (Unknown is not False);
- earlier fix attempts (a fix that did not help is evidence against its hypothesis);
- the **only** KB ids you may use, with each fix's risk, reversibility, bind names and candidate ids;
- the plan rules, this schema and an example.

Then:

1. Explain the likely cause in plain words. Be honest about the bucket:
   - Riot or account problems, ISP problems and hardware faults cannot be fixed by a PC tool;
   - for those, use guides, questions and monitoring, and say so.
2. Pick at most a few steps. Put the quick, harmless, undoable fix first. Follow it with a `monitor` step so the
   result is measured.
3. Copy fix ids and bind ids exactly from the digest. Never invent ids, paths, commands or registry keys.
4. Set `createdUtc` to now and `expiresUtc` to at most 7 days later. Make a new ULID for `planId`.
5. Output only the JSON document. The player saves it as `VTDiag-<shortid>.vtdplan` and opens it in VTDiag.

A forward-compatible field `forMachineKey` (the scan's `meta.machineKey`) is read when present. If it does not match
the player's PC, the plan is rejected with `snapshot_mismatch`. It is not in the schema yet, so do not add it until
the schema lists it.
