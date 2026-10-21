# "What's wrong?" chips. Chips set priors (category relevance); they never veto a hypothesis.
# id, order, categories, English chip text
SYMPTOMS = [
    ("stutter", 1, ["fps_performance", "network_ping", "crash_freeze_bsod"], "Stutters or freezes for a moment"),
    ("fps_low", 2, ["fps_performance", "gpu_driver", "display_resolution"], "Low or dropping FPS"),
    ("freeze_hard", 3, ["crash_freeze_bsod", "hardware_other", "gpu_driver"], "Whole PC freezes, restarts or blue-screens"),
    ("crash_desktop", 4, ["crash_freeze_bsod", "gpu_driver", "install_update_corrupt"], "Game closes or crashes to desktop"),
    ("launch_black_screen", 5, ["crash_freeze_bsod", "vanguard", "login_account_client"], "Black screen or stuck loading when starting"),
    ("wont_launch", 6, ["vanguard", "install_update_corrupt", "login_account_client", "tpm_secureboot_bios"], "Game or Riot Client won't open"),
    ("error_code", 7, ["vanguard", "tpm_secureboot_bios", "login_account_client"], "I get an error code (VAN / VAL)"),
    ("disconnect", 8, ["vanguard", "network_ping", "login_account_client"], "I get kicked or disconnected"),
    ("high_ping", 9, ["network_ping"], "High ping, lag or packet loss"),
    ("voice_chat", 10, ["audio_mic", "network_ping"], "Voice chat or microphone problems"),
    ("no_audio", 11, ["audio_mic"], "No sound, bad sound or crackling"),
    ("install_update", 12, ["install_update_corrupt", "vanguard"], "Install or update is stuck or fails"),
    ("display_issue", 13, ["display_resolution", "gpu_driver"], "Wrong resolution, black bars or screen problems"),
    ("mouse_input", 14, ["input_mouse"], "Mouse feels laggy, floaty or stops clicking"),
    ("login_issue", 15, ["login_account_client"], "Can't log in or stuck in the client"),
    ("bios_requirement", 16, ["tpm_secureboot_bios", "vanguard"], "Vanguard asks for TPM, Secure Boot or Memory Integrity"),
]
