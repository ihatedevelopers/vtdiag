# Intake questions. Answering sets signal <id> (= question id). Yes/No questions get yes/no/unsure options.
# Choice questions: options = [(option_id, sets, text)]; each option also sets "<id>.<option_id>" True.
# Fields: id, text, help, intake (asked up front), symptoms filter (empty = any), options (None = yes/no/unsure)

Q = []


def q(id, text, help=None, intake=False, symptoms=(), options=None, ask_if=()):
    Q.append(dict(id=id, text=text, help=help, intake=intake, symptoms=list(symptoms), options=options, ask_if=list(ask_if)))


q("ask.league_also_broken",
  "Is League of Legends (or another Riot game) also broken on this PC?",
  "If both Riot games fail the same way, the problem is usually Vanguard itself, not VALORANT.",
  intake=True)
q("ask.other_account_works",
  "Does a different Riot account work on this same PC?",
  "Log out, log in with a friend's or a second account, and try once. If that account works, the problem is on the account side, not your PC.",
  symptoms=["error_code", "login_issue", "launch_black_screen", "disconnect"])
q("ask.only_valorant_affected",
  "Do other games run fine, and only VALORANT has this problem?",
  "Think of another online game you play. Does it stutter or lag the same way?",
  symptoms=["stutter", "fps_low", "high_ping", "freeze_hard"])
q("ask.stutter_periodic",
  "Does the stutter repeat on a rhythm, like every few seconds?",
  "A short freeze that comes back every 2 to 60 seconds, even when nothing is happening in the game.",
  symptoms=["stutter", "fps_low"])
q("ask.whole_pc_freezes",
  "Does the whole PC freeze (mouse stuck, music stops), not only the game?",
  None,
  symptoms=["stutter", "freeze_hard", "crash_desktop"])
q("ask.vanguard_precheck_on",
  "Did you turn on Vanguard Pre-Check (Vanguard only starts when you play)?",
  "Pre-Check is an option in the Vanguard tray icon menu (Windows 11 25H2 and newer). If you can't check it, choose 'Not sure'.",
  symptoms=["error_code", "wont_launch", "launch_black_screen", "disconnect"])
q("ask.uses_bluetooth_headset",
  "Do you use a Bluetooth (wireless) headset for VALORANT?",
  None,
  symptoms=["voice_chat", "no_audio"])
q("ask.started_after_update",
  "Did it start right after a Windows, driver or VALORANT update?",
  None)
q("ask.on_console",
  "Are you playing on a console (PlayStation or Xbox)?",
  "This tool checks Windows PCs. Console errors need Riot or the console maker.",
  symptoms=["error_code", "login_issue", "disconnect"])
q("ask.friends_same_server",
  "Do your friends in the same region have the same problem right now?",
  "If many players have it at the same moment, it is usually a Riot server or internet provider problem.",
  symptoms=["high_ping", "disconnect", "login_issue", "error_code"])
q("ask.only_first_matches",
  "Is it worst in the first match or two after an update, then gets better?",
  None,
  symptoms=["stutter", "fps_low"])
q("ask.fps_drops_over_time",
  "Does FPS start fine and then drop the longer you play?",
  None,
  symptoms=["fps_low", "stutter"])
q("ask.on_wifi_or_cable", "How is this PC connected to the internet?", None,
  symptoms=["high_ping", "disconnect", "stutter", "voice_chat"],
  options=[("wifi", "true", "Wi-Fi"), ("cable", "false", "Ethernet cable"), ("unsure", "unknown", "Not sure")])
q("ask.others_cant_hear_me",
  "Is the problem that teammates can't hear you (your microphone)?",
  None,
  symptoms=["voice_chat"])
q("ask.secureboot_enable_broke_boot",
  "Did your PC stop starting (or show a Secure Boot error) after you turned Secure Boot on?",
  None,
  symptoms=["bios_requirement", "error_code", "wont_launch"])
q("ask.pc_built_recently",
  "Is this a new PC, or did you recently change the motherboard, CPU or graphics card?",
  None,
  symptoms=["stutter", "freeze_hard", "crash_desktop", "fps_low"])
