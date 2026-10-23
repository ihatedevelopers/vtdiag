# Manual ("do it yourself") steps for every fix, shown in the app and in docs/MANUAL_FIXES*.md.
# One plain sentence per step, 6th-grade reading level; same change the automated steps make.
MANUAL = {
    "fix.audio.bt_handsfree_off": {
        "en": [
            "Right-click the Start button and open \"Device Manager\".",
            "Open \"Sound, video and game controllers\" and find each Hands-Free device VTDiag named in your report.",
            "Right-click it and choose \"Disable device\", then click \"Yes\".",
            "Do the same for every Hands-Free entry in the report, then reconnect your headset.",
            "To undo, right-click the same device and choose \"Enable device\".",
        ],
        "tr": [
            "Başlat düğmesine sağ tıkla ve \"Aygıt Yöneticisi\"ni aç.",
            "\"Ses, video ve oyun denetleyicileri\"ni aç ve VTDiag raporunda adı geçen her Hands-Free aygıtını bul.",
            "Aygıta sağ tıkla, \"Cihazı devre dışı bırak\"ı seç ve \"Evet\"e bas.",
            "Rapordaki her Hands-Free kaydı için aynısını yap, sonra kulaklığını yeniden bağla.",
            "Geri almak için aynı aygıta sağ tıklayıp \"Cihazı etkinleştir\"i seç.",
        ],
    },
    "fix.display.disable_fso": {
        "en": [
            "Close VALORANT and the Riot Client.",
            "Open the folder of the VALORANT file VTDiag named in your report (VALORANT-Win64-Shipping.exe).",
            "Right-click that file and choose \"Properties\", then open the \"Compatibility\" tab.",
            "Tick \"Disable fullscreen optimizations\" and click \"OK\" (do not use \"Change settings for all users\").",
            "Start VALORANT and play one match to check.",
        ],
        "tr": [
            "VALORANT'ı ve Riot Client'ı kapat.",
            "VTDiag raporunda yazan VALORANT dosyasının (VALORANT-Win64-Shipping.exe) klasörünü aç.",
            "Dosyaya sağ tıkla, \"Özellikler\"i seç ve \"Uyumluluk\" sekmesine geç.",
            "\"Tam ekran iyileştirmelerini devre dışı bırak\" kutusunu işaretle ve \"Tamam\"a bas (\"Tüm kullanıcılar için ayarları değiştir\"i kullanma).",
            "VALORANT'ı aç ve kontrol için bir maç oyna.",
        ],
    },
    "fix.display.game_dvr_off": {
        "en": [
            "Press Win+R, type \"regedit\" and press Enter to open Registry Editor.",
            "Go to the key \"HKEY_CURRENT_USER\\System\\GameConfigStore\", right-click it and choose \"Export\" to save a backup.",
            "Double-click \"GameDVR_Enabled\", set \"Value data\" to 0 and click \"OK\".",
            "Go to \"HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR\" and export it as a backup too.",
            "Double-click \"AppCaptureEnabled\" (or create it with New > DWORD (32-bit) Value), set it to 0 and click \"OK\".",
            "Sign out and back in, then play one match; to undo, double-click your saved backup files.",
        ],
        "tr": [
            "Win+R'ye bas, \"regedit\" yaz ve Enter'a basarak Kayıt Defteri Düzenleyicisi'ni aç.",
            "\"HKEY_CURRENT_USER\\System\\GameConfigStore\" anahtarına git, sağ tıklayıp \"Dışa Aktar\" ile yedeğini al.",
            "\"GameDVR_Enabled\" değerine çift tıkla, \"Değer verisi\"ni 0 yap ve \"Tamam\"a bas.",
            "\"HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR\" anahtarına git ve onun da yedeğini al.",
            "\"AppCaptureEnabled\" değerine çift tıkla (yoksa Yeni > DWORD (32 bit) Değeri ile oluştur), 0 yap ve \"Tamam\"a bas.",
            "Oturumu kapatıp tekrar aç, bir maç oyna; geri almak için kaydettiğin yedek dosyalara çift tıkla.",
        ],
    },
    "fix.display.gpu_pref_high": {
        "en": [
            "Open Settings > System > Display > Graphics (on Windows 10 the link is called \"Graphics settings\").",
            "Click \"Browse\" and pick the VALORANT file VTDiag named in your report, or click it if it is already in the list.",
            "Click the app, then \"Options\".",
            "Choose \"High performance\" and click \"Save\".",
            "Restart VALORANT so it starts on your graphics card.",
        ],
        "tr": [
            "Ayarlar > Sistem > Ekran > Grafik'i aç (Windows 10'da bağlantının adı \"Grafik ayarları\").",
            "\"Gözat\"a bas ve VTDiag raporunda yazan VALORANT dosyasını seç; listede zaten varsa ona tıkla.",
            "Uygulamaya tıkla, sonra \"Seçenekler\"e bas.",
            "\"Yüksek performans\"ı seç ve \"Kaydet\"e bas.",
            "VALORANT'ı yeniden başlat ki ekran kartında açılsın.",
        ],
    },
    "fix.display.hags_off": {
        "en": [
            "Open Settings > System > Display > Graphics.",
            "On Windows 11, click \"Change default graphics settings\"; on Windows 10 the option is right on the \"Graphics settings\" page.",
            "Turn \"Hardware-accelerated GPU scheduling\" off.",
            "Restart the PC, then play one match.",
        ],
        "tr": [
            "Ayarlar > Sistem > Ekran > Grafik'i aç.",
            "Windows 11'de \"Varsayılan grafik ayarlarını değiştir\"e tıkla; Windows 10'da bu seçenek doğrudan \"Grafik ayarları\" sayfasında.",
            "\"Donanım hızlandırmalı GPU zamanlama\" seçeneğini kapat.",
            "Bilgisayarı yeniden başlat, sonra bir maç oyna.",
        ],
    },
    "fix.display.mpo_off": {
        "en": [
            "Press Win+R, type \"regedit\" and press Enter to open Registry Editor.",
            "Go to the key \"HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\Dwm\", right-click it and choose \"Export\" to save a backup.",
            "Right-click the empty right side and choose New > DWORD (32-bit) Value, and name it \"OverlayTestMode\" (skip this if it already exists).",
            "Double-click \"OverlayTestMode\", set \"Value data\" to 5 and click \"OK\".",
            "Restart the PC; to undo, double-click your saved backup file and restart.",
        ],
        "tr": [
            "Win+R'ye bas, \"regedit\" yaz ve Enter'a basarak Kayıt Defteri Düzenleyicisi'ni aç.",
            "\"HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\Dwm\" anahtarına git, sağ tıklayıp \"Dışa Aktar\" ile yedeğini al.",
            "Sağdaki boş alana sağ tıkla, Yeni > DWORD (32 bit) Değeri'ni seç ve adını \"OverlayTestMode\" yap (zaten varsa bu adımı geç).",
            "\"OverlayTestMode\" değerine çift tıkla, \"Değer verisi\"ni 5 yap ve \"Tamam\"a bas.",
            "Bilgisayarı yeniden başlat; geri almak için yedek dosyana çift tıklayıp yeniden başlat.",
        ],
    },
    "fix.generic.close_process": {
        "en": [
            "Find the program VTDiag named in your report.",
            "Close its window with the X button.",
            "If it still runs near the clock, right-click its tray icon and choose \"Exit\" or \"Quit\".",
            "It will start again next time you turn on your PC, so repeat this before you play if needed.",
        ],
        "tr": [
            "VTDiag raporunda adı geçen programı bul.",
            "Penceresini X düğmesiyle kapat.",
            "Saatin yanında hâlâ çalışıyorsa simgesine sağ tıkla ve \"Çıkış\" ya da \"Kapat\"ı seç.",
            "Bilgisayarı bir dahaki açışında yine başlar, gerekirse oynamadan önce bunu tekrarla.",
        ],
    },
    "fix.generic.disable_device": {
        "en": [
            "Right-click the Start button and open \"Device Manager\".",
            "Find the device VTDiag named in your report (open the group it belongs to).",
            "Right-click it, choose \"Disable device\" and click \"Yes\" (do not choose \"Uninstall device\").",
            "To undo, right-click the same device and choose \"Enable device\".",
        ],
        "tr": [
            "Başlat düğmesine sağ tıkla ve \"Aygıt Yöneticisi\"ni aç.",
            "VTDiag raporunda adı geçen aygıtı bul (ait olduğu grubu aç).",
            "Aygıta sağ tıkla, \"Cihazı devre dışı bırak\"ı seç ve \"Evet\"e bas (\"Cihazı kaldır\"ı seçme).",
            "Geri almak için aynı aygıta sağ tıklayıp \"Cihazı etkinleştir\"i seç.",
        ],
    },
    "fix.generic.disable_startup_hkcu": {
        "en": [
            "Press Ctrl+Shift+Esc to open Task Manager.",
            "On Windows 11 open \"Startup apps\"; on Windows 10 open the \"Startup\" tab.",
            "Right-click the program VTDiag named in your report and choose \"Disable\".",
            "Restart the PC; you can still open the program yourself when you need it.",
        ],
        "tr": [
            "Ctrl+Shift+Esc'ye basarak Görev Yöneticisi'ni aç.",
            "Windows 11'de \"Başlangıç uygulamaları\"nı, Windows 10'da \"Başlangıç\" sekmesini aç.",
            "VTDiag raporunda adı geçen programa sağ tıkla ve \"Devre dışı bırak\"ı seç.",
            "Bilgisayarı yeniden başlat; programı istediğinde yine kendin açabilirsin.",
        ],
    },
    "fix.generic.disable_startup_hklm": {
        "en": [
            "Press Ctrl+Shift+Esc to open Task Manager.",
            "On Windows 11 open \"Startup apps\"; on Windows 10 open the \"Startup\" tab.",
            "Right-click the program VTDiag named in your report and choose \"Disable\" (say \"Yes\" if Windows asks for admin rights).",
            "Restart the PC; the program stays installed and you can still open it yourself.",
        ],
        "tr": [
            "Ctrl+Shift+Esc'ye basarak Görev Yöneticisi'ni aç.",
            "Windows 11'de \"Başlangıç uygulamaları\"nı, Windows 10'da \"Başlangıç\" sekmesini aç.",
            "VTDiag raporunda adı geçen programa sağ tıkla ve \"Devre dışı bırak\"ı seç (yönetici izni sorarsa \"Evet\" de).",
            "Bilgisayarı yeniden başlat; program yüklü kalır ve istediğinde yine açabilirsin.",
        ],
    },
    "fix.generic.remove_compat_layer": {
        "en": [
            "Close VALORANT and the Riot Client.",
            "Open the folder of the program file VTDiag named in your report.",
            "Right-click the file, choose \"Properties\" and open the \"Compatibility\" tab.",
            "Untick only the settings listed in your report (for example \"Run this program in compatibility mode for\").",
            "Click \"OK\", then start the Riot Client again.",
        ],
        "tr": [
            "VALORANT'ı ve Riot Client'ı kapat.",
            "VTDiag raporunda yazan program dosyasının klasörünü aç.",
            "Dosyaya sağ tıkla, \"Özellikler\"i seç ve \"Uyumluluk\" sekmesine geç.",
            "Sadece raporda yazan ayarların işaretini kaldır (örneğin \"Bu programı şunun için uyumluluk modunda çalıştır\").",
            "\"Tamam\"a bas, sonra Riot Client'ı yeniden aç.",
        ],
    },
    "fix.generic.unbind_ipv6": {
        "en": [
            "Press Win+R, type \"ncpa.cpl\" and press Enter to open Network Connections.",
            "Right-click the adapter VTDiag named in your report and choose \"Properties\".",
            "Untick \"Internet Protocol Version 6 (TCP/IPv6)\" and click \"OK\".",
            "To undo, tick the same box again.",
        ],
        "tr": [
            "Win+R'ye bas, \"ncpa.cpl\" yaz ve Enter'a basarak Ağ Bağlantıları'nı aç.",
            "VTDiag raporunda adı geçen bağdaştırıcıya sağ tıkla ve \"Özellikler\"i seç.",
            "\"İnternet Protokolü Sürüm 6 (TCP/IPv6)\" kutusunun işaretini kaldır ve \"Tamam\"a bas.",
            "Geri almak için aynı kutuyu tekrar işaretle.",
        ],
    },
    "fix.input.mouse_accel_off": {
        "en": [
            "Press Win+R, type \"main.cpl\" and press Enter to open Mouse Properties.",
            "Open the \"Pointer Options\" tab.",
            "Untick \"Enhance pointer precision\" and click \"OK\".",
            "Restart the PC, then check your aim in the Range.",
        ],
        "tr": [
            "Win+R'ye bas, \"main.cpl\" yaz ve Enter'a basarak Fare Özellikleri'ni aç.",
            "\"İşaretçi Seçenekleri\" sekmesine geç.",
            "\"İşaretçi duyarlığını artır\" kutusunun işaretini kaldır ve \"Tamam\"a bas.",
            "Bilgisayarı yeniden başlat, sonra Atış Poligonu'nda nişanını dene.",
        ],
    },
    "fix.net.disable_second_adapter": {
        "en": [
            "Right-click the Start button and open \"Device Manager\".",
            "Open \"Network adapters\" and find the adapter VTDiag named in your report.",
            "Right-click it, choose \"Disable device\" and click \"Yes\".",
            "Check that your internet still works; to undo, right-click it and choose \"Enable device\".",
        ],
        "tr": [
            "Başlat düğmesine sağ tıkla ve \"Aygıt Yöneticisi\"ni aç.",
            "\"Ağ bağdaştırıcıları\"nı aç ve VTDiag raporunda adı geçen bağdaştırıcıyı bul.",
            "Ona sağ tıkla, \"Cihazı devre dışı bırak\"ı seç ve \"Evet\"e bas.",
            "İnternetin hâlâ çalıştığını kontrol et; geri almak için sağ tıklayıp \"Cihazı etkinleştir\"i seç.",
        ],
    },
    "fix.net.disable_unused_adapter": {
        "en": [
            "Right-click the Start button and open \"Device Manager\".",
            "Open \"Network adapters\" and find the Realtek adapter VTDiag named in your report.",
            "Right-click it, choose \"Disable device\" and click \"Yes\".",
            "Check that your internet still works; to undo, right-click it and choose \"Enable device\".",
        ],
        "tr": [
            "Başlat düğmesine sağ tıkla ve \"Aygıt Yöneticisi\"ni aç.",
            "\"Ağ bağdaştırıcıları\"nı aç ve VTDiag raporunda adı geçen Realtek bağdaştırıcısını bul.",
            "Ona sağ tıkla, \"Cihazı devre dışı bırak\"ı seç ve \"Evet\"e bas.",
            "İnternetin hâlâ çalıştığını kontrol et; geri almak için sağ tıklayıp \"Cihazı etkinleştir\"i seç.",
        ],
    },
    "fix.net.dns_flush": {
        "en": [
            "Right-click the Start button and open \"Terminal (Admin)\" (on Windows 10: \"Windows PowerShell (Admin)\").",
            "Type \"ipconfig /flushdns\" and press Enter.",
            "Wait for the message that the DNS cache was flushed, then start the Riot Client.",
        ],
        "tr": [
            "Başlat düğmesine sağ tıkla ve \"Terminal (Yönetici)\"yi aç (Windows 10'da: \"Windows PowerShell (Yönetici)\").",
            "\"ipconfig /flushdns\" yaz ve Enter'a bas.",
            "DNS önbelleğinin temizlendiği mesajını bekle, sonra Riot Client'ı aç.",
        ],
    },
    "fix.net.realtek_power_saving_off": {
        "en": [
            "Right-click the Start button and open \"Device Manager\".",
            "Open \"Network adapters\", right-click the adapter VTDiag named in your report and choose \"Properties\".",
            "Open the \"Advanced\" tab.",
            "Set each of these to \"Disabled\" if you see it: \"Energy-Efficient Ethernet\", \"Green Ethernet\", \"Advanced EEE\", \"Gigabit Lite\".",
            "Click \"OK\"; your connection may drop for a few seconds and then come back.",
        ],
        "tr": [
            "Başlat düğmesine sağ tıkla ve \"Aygıt Yöneticisi\"ni aç.",
            "\"Ağ bağdaştırıcıları\"nı aç, VTDiag raporunda adı geçen bağdaştırıcıya sağ tıkla ve \"Özellikler\"i seç.",
            "\"Gelişmiş\" sekmesine geç.",
            "Bunlardan hangisini görürsen \"Disabled\" (Devre Dışı) yap: \"Energy-Efficient Ethernet\", \"Green Ethernet\", \"Advanced EEE\", \"Gigabit Lite\".",
            "\"Tamam\"a bas; bağlantın birkaç saniye kopup geri gelebilir.",
        ],
    },
    "fix.net.unbind_ipv6_active": {
        "en": [
            "Press Win+R, type \"ncpa.cpl\" and press Enter to open Network Connections.",
            "Right-click the connection you use (the one VTDiag named in your report) and choose \"Properties\".",
            "Untick \"Internet Protocol Version 6 (TCP/IPv6)\" and click \"OK\".",
            "Play one match with voice chat; to undo, tick the same box again.",
        ],
        "tr": [
            "Win+R'ye bas, \"ncpa.cpl\" yaz ve Enter'a basarak Ağ Bağlantıları'nı aç.",
            "Kullandığın bağlantıya (VTDiag raporunda adı geçen) sağ tıkla ve \"Özellikler\"i seç.",
            "\"İnternet Protokolü Sürüm 6 (TCP/IPv6)\" kutusunun işaretini kaldır ve \"Tamam\"a bas.",
            "Sesli sohbetle bir maç oyna; geri almak için aynı kutuyu tekrar işaretle.",
        ],
    },
    "fix.net.winsock_reset": {
        "en": [
            "Right-click the Start button and open \"Terminal (Admin)\" (on Windows 10: \"Windows PowerShell (Admin)\").",
            "Type \"netsh winsock reset\" and press Enter.",
            "Restart the PC when it says so.",
            "If a VPN or network tool stops working afterwards, reinstall it.",
        ],
        "tr": [
            "Başlat düğmesine sağ tıkla ve \"Terminal (Yönetici)\"yi aç (Windows 10'da: \"Windows PowerShell (Yönetici)\").",
            "\"netsh winsock reset\" yaz ve Enter'a bas.",
            "İstediğinde bilgisayarı yeniden başlat.",
            "Sonrasında bir VPN ya da ağ aracı çalışmazsa onu yeniden yükle.",
        ],
    },
    "fix.os.fast_startup_off": {
        "en": [
            "Press Win+R, type \"powercfg.cpl\" and press Enter to open Power Options.",
            "Click \"Choose what the power buttons do\" on the left.",
            "Click \"Change settings that are currently unavailable\".",
            "Untick \"Turn on fast startup (recommended)\" and click \"Save changes\".",
            "Restart the PC once.",
        ],
        "tr": [
            "Win+R'ye bas, \"powercfg.cpl\" yaz ve Enter'a basarak Güç Seçenekleri'ni aç.",
            "Soldaki \"Güç düğmelerinin yapacaklarını seçin\"e tıkla.",
            "\"Şu anda kullanılamayan ayarları değiştirin\"e tıkla.",
            "\"Hızlı başlatmayı aç (önerilir)\" kutusunun işaretini kaldır ve \"Değişiklikleri kaydet\"e bas.",
            "Bilgisayarı bir kez yeniden başlat.",
        ],
    },
    "fix.os.time_resync": {
        "en": [
            "Open Settings > Time & language > Date & time.",
            "Make sure \"Set time automatically\" is on, then click \"Sync now\".",
            "Or open \"Terminal (Admin)\" and type \"w32tm /resync\", then press Enter.",
            "Check that the clock is right, then start the Riot Client.",
        ],
        "tr": [
            "Ayarlar > Saat ve dil > Tarih ve saat'i aç.",
            "\"Saati otomatik olarak ayarla\" açık olsun, sonra \"Şimdi eşitle\"ye bas.",
            "Ya da \"Terminal (Yönetici)\"yi aç, \"w32tm /resync\" yaz ve Enter'a bas.",
            "Saatin doğru olduğunu kontrol et, sonra Riot Client'ı aç.",
        ],
    },
    "fix.power.cpu_max_100": {
        "en": [
            "Press Win+R, type \"powercfg.cpl\" and press Enter to open Power Options.",
            "Next to the plan you use, click \"Change plan settings\", then \"Change advanced power settings\".",
            "Open \"Processor power management\" > \"Maximum processor state\".",
            "Set \"Plugged in\" (or \"Setting\" on desktops) to 100% and click \"OK\".",
        ],
        "tr": [
            "Win+R'ye bas, \"powercfg.cpl\" yaz ve Enter'a basarak Güç Seçenekleri'ni aç.",
            "Kullandığın planın yanındaki \"Plan ayarlarını değiştir\"e, sonra \"Gelişmiş güç ayarlarını değiştir\"e tıkla.",
            "\"İşlemci güç yönetimi\" > \"En yüksek işlemci durumu\"nu aç.",
            "\"Prize takılı\" (masaüstünde \"Ayar\") değerini %100 yap ve \"Tamam\"a bas.",
        ],
    },
    "fix.power.high_performance": {
        "en": [
            "Right-click the Start button and open \"Terminal (Admin)\" (on Windows 10: \"Windows PowerShell (Admin)\").",
            "Type \"powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c\" and press Enter.",
            "Press Win+R, type \"powercfg.cpl\" and check that \"High performance\" is now selected.",
            "To undo, pick your old plan in the same window.",
        ],
        "tr": [
            "Başlat düğmesine sağ tıkla ve \"Terminal (Yönetici)\"yi aç (Windows 10'da: \"Windows PowerShell (Yönetici)\").",
            "\"powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c\" yaz ve Enter'a bas.",
            "Win+R'ye bas, \"powercfg.cpl\" yaz ve \"Yüksek performans\"ın seçili olduğunu kontrol et.",
            "Geri almak için aynı pencerede eski planını seç.",
        ],
    },
    "fix.power.usb_selective_suspend_off": {
        "en": [
            "Press Win+R, type \"powercfg.cpl\" and press Enter to open Power Options.",
            "Next to the plan you use, click \"Change plan settings\", then \"Change advanced power settings\".",
            "Open \"USB settings\" > \"USB selective suspend setting\".",
            "Set it to \"Disabled\" (both \"On battery\" and \"Plugged in\" if you see two) and click \"OK\".",
        ],
        "tr": [
            "Win+R'ye bas, \"powercfg.cpl\" yaz ve Enter'a basarak Güç Seçenekleri'ni aç.",
            "Kullandığın planın yanındaki \"Plan ayarlarını değiştir\"e, sonra \"Gelişmiş güç ayarlarını değiştir\"e tıkla.",
            "\"USB ayarları\" > \"USB seçici askıya alma ayarı\"nı aç.",
            "Bunu \"Devre Dışı\" yap (iki satır görürsen hem \"Pilde\" hem \"Prize takılı\") ve \"Tamam\"a bas.",
        ],
    },
    "fix.proc.close_overlays": {
        "en": [
            "Find each overlay or recording program VTDiag named in your report.",
            "Close its window with the X button.",
            "If it still runs near the clock, right-click its tray icon and choose \"Exit\" or \"Quit\".",
            "Start VALORANT and play one match; the programs start again next time you turn on your PC.",
        ],
        "tr": [
            "VTDiag raporunda adı geçen her overlay ya da kayıt programını bul.",
            "Penceresini X düğmesiyle kapat.",
            "Saatin yanında hâlâ çalışıyorsa simgesine sağ tıkla ve \"Çıkış\" ya da \"Kapat\"ı seç.",
            "VALORANT'ı aç ve bir maç oyna; bu programlar bilgisayarı bir dahaki açışında yine başlar.",
        ],
    },
    "fix.riot.client_runasadmin": {
        "en": [
            "Close VALORANT and the Riot Client.",
            "Open the folder of RiotClientServices.exe that VTDiag named in your report.",
            "Right-click RiotClientServices.exe, choose \"Properties\" and open the \"Compatibility\" tab.",
            "Tick \"Run this program as an administrator\" and click \"OK\" (do not use \"Change settings for all users\").",
            "Start the Riot Client again and say \"Yes\" when Windows asks.",
        ],
        "tr": [
            "VALORANT'ı ve Riot Client'ı kapat.",
            "VTDiag raporunda yazan RiotClientServices.exe dosyasının klasörünü aç.",
            "RiotClientServices.exe'ye sağ tıkla, \"Özellikler\"i seç ve \"Uyumluluk\" sekmesine geç.",
            "\"Bu programı yönetici olarak çalıştır\" kutusunu işaretle ve \"Tamam\"a bas (\"Tüm kullanıcılar için ayarları değiştir\"i kullanma).",
            "Riot Client'ı yeniden aç ve Windows sorduğunda \"Evet\" de.",
        ],
    },
    "fix.sec.remove_riot_perfoptions": {
        "en": [
            "Press Win+R, type \"regedit\" and press Enter to open Registry Editor.",
            "Go to \"HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\\" plus the program name VTDiag named, then \"PerfOptions\".",
            "Right-click the \"PerfOptions\" key and choose \"Export\" to save a backup.",
            "In the right side, delete only the values \"CpuPriorityClass\" and \"IoPriority\" (keep the key itself).",
            "Do the same for each program in the report, then restart the PC.",
        ],
        "tr": [
            "Win+R'ye bas, \"regedit\" yaz ve Enter'a basarak Kayıt Defteri Düzenleyicisi'ni aç.",
            "\"HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\\" altında VTDiag'ın yazdığı programa, sonra \"PerfOptions\"a git.",
            "\"PerfOptions\" anahtarına sağ tıkla ve \"Dışa Aktar\" ile yedeğini al.",
            "Sağ tarafta sadece \"CpuPriorityClass\" ve \"IoPriority\" değerlerini sil (anahtarın kendisini silme).",
            "Rapordaki her program için aynısını yap, sonra bilgisayarı yeniden başlat.",
        ],
    },
    "fix.sec.reset_exploit_overrides": {
        "en": [
            "Open Windows Security > App & browser control > Exploit protection settings.",
            "Open the \"Program settings\" tab.",
            "Click the program VTDiag named in your report (for example vgc.exe) and click \"Remove\".",
            "Do the same for each program in the report, then restart the PC.",
        ],
        "tr": [
            "Windows Güvenliği > Uygulama ve tarayıcı denetimi > Açıklardan yararlanma koruması ayarları'nı aç.",
            "\"Program ayarları\" sekmesine geç.",
            "VTDiag raporunda adı geçen programa (örneğin vgc.exe) tıkla ve \"Kaldır\"a bas.",
            "Rapordaki her program için aynısını yap, sonra bilgisayarı yeniden başlat.",
        ],
    },
    "fix.svc.disable_conflict_service": {
        "en": [
            "Press Win+R, type \"services.msc\" and press Enter to open Services.",
            "Double-click the service VTDiag named in your report (check that \"Service name\" matches).",
            "Set \"Startup type\" to \"Disabled\" and click \"Apply\".",
            "Click \"Stop\", then \"OK\".",
            "To undo, set \"Startup type\" back to what it was before.",
        ],
        "tr": [
            "Win+R'ye bas, \"services.msc\" yaz ve Enter'a basarak Hizmetler'i aç.",
            "VTDiag raporunda adı geçen hizmete çift tıkla (\"Hizmet adı\"nın aynı olduğunu kontrol et).",
            "\"Başlangıç türü\"nü \"Devre Dışı\" yap ve \"Uygula\"ya bas.",
            "\"Durdur\"a, sonra \"Tamam\"a bas.",
            "Geri almak için \"Başlangıç türü\"nü eski haline getir.",
        ],
    },
    "fix.vg.quarantine_bootstatus": {
        "en": [
            "Close VALORANT and the Riot Client, including its icon near the clock.",
            "Make a new folder on your Desktop, for example \"Vanguard backup\".",
            "Open the folder where VTDiag found vgkbootstatus.dat (the path is in your report).",
            "Move the file into your Desktop folder with Cut and Paste, and click \"Continue\" if Windows asks (do not delete it).",
            "Restart the PC, then start VALORANT.",
        ],
        "tr": [
            "VALORANT'ı ve Riot Client'ı kapat, saatin yanındaki simgesi de dahil.",
            "Masaüstünde yeni bir klasör oluştur, örneğin \"Vanguard yedek\".",
            "VTDiag'ın vgkbootstatus.dat dosyasını bulduğu klasörü aç (yol raporunda yazıyor).",
            "Dosyayı Kes ve Yapıştır ile masaüstündeki klasöre taşı, Windows sorarsa \"Devam\"a bas (silme).",
            "Bilgisayarı yeniden başlat, sonra VALORANT'ı aç.",
        ],
    },
    "fix.vg.quarantine_stale_dat": {
        "en": [
            "Close VALORANT and the Riot Client, including its icon near the clock.",
            "Make a new folder on your Desktop, for example \"Vanguard backup\".",
            "Open the folder where VTDiag found 392667600.dat (the path is in your report).",
            "Move the file into your Desktop folder with Cut and Paste, and click \"Continue\" if Windows asks (do not delete it).",
            "Restart the PC, then start VALORANT.",
        ],
        "tr": [
            "VALORANT'ı ve Riot Client'ı kapat, saatin yanındaki simgesi de dahil.",
            "Masaüstünde yeni bir klasör oluştur, örneğin \"Vanguard yedek\".",
            "VTDiag'ın 392667600.dat dosyasını bulduğu klasörü aç (yol raporunda yazıyor).",
            "Dosyayı Kes ve Yapıştır ile masaüstündeki klasöre taşı, Windows sorarsa \"Devam\"a bas (silme).",
            "Bilgisayarı yeniden başlat, sonra VALORANT'ı aç.",
        ],
    },
    "fix.vg.vgc_automatic": {
        "en": [
            "Press Win+R, type \"services.msc\" and press Enter to open Services.",
            "Double-click \"vgc\".",
            "Set \"Startup type\" to \"Automatic\" and click \"Apply\".",
            "Click \"Start\", then \"OK\".",
            "Restart the PC, then start VALORANT.",
        ],
        "tr": [
            "Win+R'ye bas, \"services.msc\" yaz ve Enter'a basarak Hizmetler'i aç.",
            "\"vgc\" hizmetine çift tıkla.",
            "\"Başlangıç türü\"nü \"Otomatik\" yap ve \"Uygula\"ya bas.",
            "\"Başlat\"a, sonra \"Tamam\"a bas.",
            "Bilgisayarı yeniden başlat, sonra VALORANT'ı aç.",
        ],
    },
}
