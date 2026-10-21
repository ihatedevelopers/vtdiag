# Privacy in VTDiag

VTDiag is a free community tool. It is not affiliated with Riot Games. Nothing it collects leaves your PC unless
you export it yourself, and every export is redacted first.

## What is collected

Technical state only:

- Windows version, hardware, drivers, devices, services and process names.
- Vanguard readiness: TPM, Secure Boot, UEFI/GPT, Memory Integrity, virtualization.
- Network adapters and their settings, power and display settings, installed apps and startup entries.
- Event-log counts by provider and id, with a short list of allowed fields.
- Riot log digests: pattern ids and counts, never whole log lines.

## What is never collected

- Passwords, Wi-Fi keys and Riot tokens.
- Full process command lines, because they carry Riot auth tokens.
- Vanguard log contents. Only file names, sizes and times are read; the contents are encrypted and never opened.
- Browser data and documents.
- Your public IP, Riot ID or PUUID.
- Serial numbers (BIOS, disk, TPM, board).

Log readers drop any line with `-remoting-auth-token`, `-subject`, `CommandLine`, `MachineId`, `LoginId`, `UserName`,
`puuid`, `Bearer`, `access_token` or a JWT before matching.

## Two redaction levels

| Level | Where | What it does |
|---|---|---|
| Local | Data kept on this PC under `%ProgramData%\VTDiag` | Removes secrets only (tokens, passwords, ids next to their key). |
| Share | Everything that leaves the PC: HTML/Markdown/JSON report, `.vtdscan`, "Copy for AI" | Everything in the table below. |

Every string field in the data model carries a `[Sensitivity]` label, and the redactor follows it. A contract test
fails if a new field has no label.

| Label | Share treatment | Example |
|---|---|---|
| Identifier | Removed | Network interface GUID |
| DeviceId | Bus type and VEN/DEV kept; the instance part is replaced by a salted hash | `PCI\VEN_10EC&DEV_8125&SUBSYS_87D71043&REV_05\h:8255a5a5e05ee9d5` |
| Path | `C:\Users\<you>\…` becomes `%USERPROFILE%\…` | `%USERPROFILE%\AppData\Local\Discord` |
| Hashed | `h:` plus a salted hash | Wi-Fi name, audio device names |
| MacAddress | Vendor prefix (OUI) kept, rest hashed | `04:42:1A:h:ee60a3cfc7d2` |
| IpAddress | Reduced to a class | `private/24`, `cgnat`, `public` |
| FreeText | Text scrub (below) | Event data, device friendly names, notes |

The game server IP is never shared, only its region (for example "AWS Frankfurt"). The one exception is when you tick
"include for ISP ticket".

**The text scrub** runs on every string, whatever its label. It removes:

- e-mails, user SIDs and JWTs;
- `--remoting-auth-token=…`, `password=…`, `token=…`, `Bearer …`, Basic credentials and the Riot Client lockfile password;
- ids written next to their key, such as `puuid=…`, `LoginId=…` and `UserName=…`;
- `SSID=…`, `LocalIp=…` and MAC addresses;
- profile paths in every form (`C:\Users\…`, `C:/Users/…`, `%SystemDrive%\Users\…`, UNC paths, `\\?\…`);
- the organization name in OneDrive folders and Windows' default computer names (`DESKTOP-XXXXXXX`).

**Identifier values are removed wherever they appear.** These values become `[id]` in all text:

- the computer name and your user name (the engine passes them in);
- every identifier, SSID, MAC and IP found in the data;
- every user name seen in a profile path.

Matching is whole-word and ignores case, so a user called "Ali" does not damage "Alienware". The Turkish I, ı, İ and i
count as the same letter.

**The salt.** Hashes use a random salt made at install time. The salt never leaves your PC, so:

- a helper can tell that two reports came from the same PC, or that two lines name the same device;
- nobody can work back to the original values.

## The preview and leaving sections out

Before anything is saved, the export screen shows exactly what is in the file. You can untick whole sections:

- any snapshot section: `apps`, `processes`, `startup`, `services`, `devices`, `drivers`, `events`, `network`, `audio`,
  `display`, `power`, `input`, `optionalFeatures`, `storage`, `security`, `firmware`, `hardware`, `os`, `riot`, `pack`
  or `runs`;
- or `analysis`, `attempts` and `monitor`.

What happens to a section you left out:

- a section that is a single fact becomes "couldn't check: left out for privacy";
- in a grouped section (for example `network`), every fact inside becomes "left out" and its lists are emptied.

The report lists what you left out.

## The sweep that blocks exports

After redaction the finished output (the JSON, and each piece of the HTML, Markdown and AI digest as a reader sees it)
is swept again. The sweep looks for:

- planted test canaries (`VTDCANARY`);
- JWTs, e-mails and user SIDs;
- `C:\Users\<name>` paths;
- auth tokens and key=value secrets;
- ids next to their key;
- MAC addresses and raw network ids.

**Any hit blocks the export. Nothing is saved or copied.** The result names:

- the rule that hit;
- the JSON pointer of the field;
- the section the field is in.

It never includes the matched text. The app then offers "leave this section out and try again".

The sweep is tested against real technical data, so these never trigger it:

- product GUIDs (`HKLM:{GUID}`);
- ULIDs, SHA-256 hashes, driver file names and version numbers;
- well-known SIDs such as `S-1-5-18`.

The tests plant every canary in the free-text fields of the fixture PCs. They check that no export contains one, and
that a planted slip blocks the export and names its section.
