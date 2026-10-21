# Guides C: small flows not covered by guides_a.py / guides_b.py. Same format (every text is ("English", "Türkçe")).

GUIDES = [
    dict(
        id="guide.clock.fix",
        title=("Set your PC clock right", "Bilgisayarının saatini düzelt"),
        summary=("A wrong date, time or time zone breaks secure connections, so the Riot Client and Vanguard can't connect. About 2 minutes.",
                 "Yanlış tarih, saat ya da saat dilimi güvenli bağlantıları bozar; Riot Client ve Vanguard bağlanamaz. Yaklaşık 2 dakika."),
        bucket="guidedManual", risk="low", reboot="none",
        applies=["wrong-system-clock-offline"],
        outcome=("Does the Riot Client log in now?", "Riot Client şimdi giriş yapıyor mu?"),
        branches=[
            dict(id="skewed", when=[("os.clock_skewed", "true")],
                 text=("Your PC clock is more than 5 minutes off.", "Bilgisayarının saati 5 dakikadan fazla kaymış."),
                 steps=[
                     dict(text=("Let VTDiag sync the clock with Microsoft's time server.",
                                "Saati Microsoft'un saat sunucusuyla VTDiag eşitlesin."),
                          action="runFix", target="fix.os.time_resync"),
                     dict(text=("Open Settings > Time & language > Date & time.", "Ayarlar > Saat ve dil > Tarih ve saat'i aç."),
                          action="openSettings", target="ms-settings:dateandtime"),
                     dict(text=("Turn on 'Set time automatically' and 'Set time zone automatically'.",
                                "'Saati otomatik olarak ayarla' ve 'Saat dilimini otomatik olarak ayarla'yı aç."),
                          action="none"),
                     dict(text=("Check that the time zone shown is where you live, then restart the Riot Client.",
                                "Gösterilen saat diliminin yaşadığın yer olduğunu kontrol et, sonra Riot Client'ı yeniden başlat."),
                          action="none"),
                 ],
                 verify=[("os.clock_skewed", "false")]),
            dict(id="general", when=[],
                 text=("Make sure Windows keeps the time right by itself.", "Windows'un saati kendi kendine doğru tuttuğundan emin ol."),
                 steps=[
                     dict(text=("Open Settings > Time & language > Date & time.", "Ayarlar > Saat ve dil > Tarih ve saat'i aç."),
                          action="openSettings", target="ms-settings:dateandtime"),
                     dict(text=("Turn on 'Set time automatically', then click 'Sync now'.",
                                "'Saati otomatik olarak ayarla'yı aç, sonra 'Şimdi eşitle'ye tıkla."),
                          action="none"),
                     dict(text=("If the clock is wrong again after every restart, the small battery on the motherboard (CMOS) may be empty. A PC shop can replace it cheaply.",
                                "Saat her yeniden başlatmadan sonra yine yanlışsa, anakarttaki küçük pil (CMOS) bitmiş olabilir. Bir bilgisayarcı ucuza değiştirebilir."),
                          action="none"),
                 ]),
        ],
    ),
]
