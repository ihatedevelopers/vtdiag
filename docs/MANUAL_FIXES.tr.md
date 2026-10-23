# Kendin düzelt

VTDiag'ın önerebileceği her düzeltmenin elle yapılışı. VTDiag v0.1 sadece tarama yapar; raporunda geçen düzeltmeler için bu adımları kullan. Her seferinde tek bir şeyi değiştir ve sıradakine geçmeden önce bir maç oyna.

<!-- Bu dosya `tools/kb_builder.py` tarafından `tools/kb_curation/manual_steps.py` dosyasından üretilir. Elle düzenleme. -->

## Bluetooth kulaklığının 'telefon görüşmesi' moduna geçmesini engelle

`fix.audio.bt_handsfree_off`

VALORANT sesli sohbeti Bluetooth kulaklığının mikrofonunu açınca Windows kulaklığı Hands-Free moduna alır ve ses eski bir telefon görüşmesi gibi çıkar. Hands-Free modunu kapatacağız, böylece oyun sesi net kalır.

**Yönetici izni gerekir**

1. Başlat düğmesine sağ tıkla ve "Aygıt Yöneticisi"ni aç.
2. "Ses, video ve oyun denetleyicileri"ni aç ve VTDiag raporunda adı geçen her Hands-Free aygıtını bul.
3. Aygıta sağ tıkla, "Cihazı devre dışı bırak"ı seç ve "Evet"e bas.
4. Rapordaki her Hands-Free kaydı için aynısını yap, sonra kulaklığını yeniden bağla.
5. Geri almak için aynı aygıta sağ tıklayıp "Cihazı etkinleştir"i seç.

**Geri alma:** Geri Al'a bas, Hands-Free modu geri gelir.

## VALORANT için tam ekran iyileştirmelerini kapat

`fix.display.disable_fso`

Windows 'tam ekran iyileştirmeleri' oyunun ekrana nasıl verileceğini değiştirir. Bazı oyuncularda bu takılmaya ya da alt-tab donmalarına yol açar. Bunu sadece VALORANT için kapatacağız.

**Yönetici izni gerekir**

1. VALORANT'ı ve Riot Client'ı kapat.
2. VTDiag raporunda yazan VALORANT dosyasının (VALORANT-Win64-Shipping.exe) klasörünü aç.
3. Dosyaya sağ tıkla, "Özellikler"i seç ve "Uyumluluk" sekmesine geç.
4. "Tam ekran iyileştirmelerini devre dışı bırak" kutusunu işaretle ve "Tamam"a bas ("Tüm kullanıcılar için ayarları değiştir"i kullanma).
5. VALORANT'ı aç ve kontrol için bir maç oyna.

**Geri alma:** Geri Al'a bas, ayar kaldırılır.

## Xbox Game Bar arka plan kaydını kapat

`fix.display.game_dvr_off`

Game Bar, oynayışını arka planda sürekli kaydedebilir. Bu kayıt FPS'ten yer ve takılmaya yol açabilir. Arka plan kaydını kapatacağız (Game Bar'ın kendisi kalır).

1. Win+R'ye bas, "regedit" yaz ve Enter'a basarak Kayıt Defteri Düzenleyicisi'ni aç.
2. "HKEY_CURRENT_USER\System\GameConfigStore" anahtarına git, sağ tıklayıp "Dışa Aktar" ile yedeğini al.
3. "GameDVR_Enabled" değerine çift tıkla, "Değer verisi"ni 0 yap ve "Tamam"a bas.
4. "HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR" anahtarına git ve onun da yedeğini al.
5. "AppCaptureEnabled" değerine çift tıkla (yoksa Yeni > DWORD (32 bit) Değeri ile oluştur), 0 yap ve "Tamam"a bas.
6. Oturumu kapatıp tekrar aç, bir maç oyna; geri almak için kaydettiğin yedek dosyalara çift tıkla.

**Geri alma:** Geri Al'a bas, arka plan kaydı geri gelir.

## VALORANT'ı ekran kartında çalıştır

`fix.display.gpu_pref_high`

Bilgisayarında iki ekran kartı var ve Windows VALORANT'ı daha zayıf olan yerleşik olanda çalıştırıyor olabilir. Windows'a VALORANT için hep güçlü ekran kartını kullanmasını söyleyeceğiz.

**Yönetici izni gerekir**

1. Ayarlar > Sistem > Ekran > Grafik'i aç (Windows 10'da bağlantının adı "Grafik ayarları").
2. "Gözat"a bas ve VTDiag raporunda yazan VALORANT dosyasını seç; listede zaten varsa ona tıkla.
3. Uygulamaya tıkla, sonra "Seçenekler"e bas.
4. "Yüksek performans"ı seç ve "Kaydet"e bas.
5. VALORANT'ı yeniden başlat ki ekran kartında açılsın.

**Geri alma:** Geri Al'a bas, Windows yine kendisi seçer.

## Donanım hızlandırmalı GPU zamanlamasını kapat

`fix.display.hags_off`

Donanım hızlandırmalı GPU zamanlaması (HAGS) bir Windows grafik seçeneğidir. Bazı sürücülerle VALORANT'ta takılma, giriş gecikmesi ya da donma yapar. Bunu kapatacağız. Yeniden başlatınca geçerli olur.

**Yönetici izni gerekir** · **Yeniden başlatma gerekir**

1. Ayarlar > Sistem > Ekran > Grafik'i aç.
2. Windows 11'de "Varsayılan grafik ayarlarını değiştir"e tıkla; Windows 10'da bu seçenek doğrudan "Grafik ayarları" sayfasında.
3. "Donanım hızlandırmalı GPU zamanlama" seçeneğini kapat.
4. Bilgisayarı yeniden başlat, sonra bir maç oyna.

**Geri alma:** Geri Al'a bas ve tekrar açmak için bilgisayarı yeniden başlat.

## Multi-Plane Overlay'i (MPO) kapat

`fix.display.mpo_off`

Multi-Plane Overlay, bazı ekran kartı sürücülerinin kötü idare ettiği bir Windows görüntü özelliğidir; titremeye, alt-tab donmalarına ya da mikro takılmalara yol açabilir. NVIDIA'nın da önerdiği ayarla bunu kapatacağız.

**Yönetici izni gerekir** · **Yeniden başlatma gerekir**

1. Win+R'ye bas, "regedit" yaz ve Enter'a basarak Kayıt Defteri Düzenleyicisi'ni aç.
2. "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Dwm" anahtarına git, sağ tıklayıp "Dışa Aktar" ile yedeğini al.
3. Sağdaki boş alana sağ tıkla, Yeni > DWORD (32 bit) Değeri'ni seç ve adını "OverlayTestMode" yap (zaten varsa bu adımı geç).
4. "OverlayTestMode" değerine çift tıkla, "Değer verisi"ni 5 yap ve "Tamam"a bas.
5. Bilgisayarı yeniden başlat; geri almak için yedek dosyana çift tıklayıp yeniden başlat.

**Geri alma:** Geri Al'a bas ve tekrar açmak için bilgisayarı yeniden başlat.

## Bu programı bu oturum için kapat

`fix.generic.close_process`

Bu programın VALORANT ile çakıştığı biliniyor. Ondan kapanmasını isteyeceğiz (X'ine basmak gibi). Bilgisayarı bir sonraki açışında yine başlar.

1. VTDiag raporunda adı geçen programı bul.
2. Penceresini X düğmesiyle kapat.
3. Saatin yanında hâlâ çalışıyorsa simgesine sağ tıkla ve "Çıkış" ya da "Kapat"ı seç.
4. Bilgisayarı bir dahaki açışında yine başlar, gerekirse oynamadan önce bunu tekrarla.

**Geri alma:** Geri alınacak bir şey yok: programı istediğin zaman tekrar açabilirsin.

## Bu cihazı kapat

`fix.generic.disable_device`

Bu cihazın ya da sürücüsünün VALORANT ile çakıştığı biliniyor. Aygıt Yöneticisi'nde kapatacağız. Hiçbir şey silinmez.

**Yönetici izni gerekir**

1. Başlat düğmesine sağ tıkla ve "Aygıt Yöneticisi"ni aç.
2. VTDiag raporunda adı geçen aygıtı bul (ait olduğu grubu aç).
3. Aygıta sağ tıkla, "Cihazı devre dışı bırak"ı seç ve "Evet"e bas ("Cihazı kaldır"ı seçme).
4. Geri almak için aynı aygıta sağ tıklayıp "Cihazı etkinleştir"i seç.

**Geri alma:** Geri Al'a bas, cihaz tekrar açılır.

## Bu programın Windows ile başlamasını engelle

`fix.generic.disable_startup_hkcu`

Bu program her oturum açtığında başlar ve VALORANT ile çakışabilir. Otomatik başlamasını kapatacağız; program kurulu kalır, yine kendin açabilirsin.

1. Ctrl+Shift+Esc'ye basarak Görev Yöneticisi'ni aç.
2. Windows 11'de "Başlangıç uygulamaları"nı, Windows 10'da "Başlangıç" sekmesini aç.
3. VTDiag raporunda adı geçen programa sağ tıkla ve "Devre dışı bırak"ı seç.
4. Bilgisayarı yeniden başlat; programı istediğinde yine kendin açabilirsin.

**Geri alma:** Geri Al'a bas, program yine Windows ile başlar.

## Bu programın Windows ile başlamasını engelle (tüm kullanıcılar)

`fix.generic.disable_startup_hklm`

Bu program Windows açılırken tüm kullanıcılar için başlar ve VALORANT ile çakışabilir. Otomatik başlamasını kapatacağız; program kurulu kalır.

**Yönetici izni gerekir**

1. Ctrl+Shift+Esc'ye basarak Görev Yöneticisi'ni aç.
2. Windows 11'de "Başlangıç uygulamaları"nı, Windows 10'da "Başlangıç" sekmesini aç.
3. VTDiag raporunda adı geçen programa sağ tıkla ve "Devre dışı bırak"ı seç (yönetici izni sorarsa "Evet" de).
4. Bilgisayarı yeniden başlat; program yüklü kalır ve istediğinde yine açabilirsin.

**Geri alma:** Geri Al'a bas, program yine Windows ile başlar.

## Bir programdan eski uyumluluk ayarlarını kaldır

`fix.generic.remove_compat_layer`

Bir programda eski Windows uyumluluk ayarları var ('Windows 8 modunda çalıştır' gibi). Bunlar VALORANT'ın ya da Riot Client'ın açılmasını engelleyebilir. Listelenen ayarları kaldıracağız.

**Yönetici izni gerekir**

1. VALORANT'ı ve Riot Client'ı kapat.
2. VTDiag raporunda yazan program dosyasının klasörünü aç.
3. Dosyaya sağ tıkla, "Özellikler"i seç ve "Uyumluluk" sekmesine geç.
4. Sadece raporda yazan ayarların işaretini kaldır (örneğin "Bu programı şunun için uyumluluk modunda çalıştır").
5. "Tamam"a bas, sonra Riot Client'ı yeniden aç.

**Geri alma:** Geri Al'a bas, ayarlar geri gelir.

## Bu bağdaştırıcıda IPv6'yı kapat

`fix.generic.unbind_ipv6`

IPv6'yı sadece bu ağ bağdaştırıcısında kapatacağız. İnternet IPv4 üzerinden çalışmaya devam eder.

**Yönetici izni gerekir**

1. Win+R'ye bas, "ncpa.cpl" yaz ve Enter'a basarak Ağ Bağlantıları'nı aç.
2. VTDiag raporunda adı geçen bağdaştırıcıya sağ tıkla ve "Özellikler"i seç.
3. "İnternet Protokolü Sürüm 6 (TCP/IPv6)" kutusunun işaretini kaldır ve "Tamam"a bas.
4. Geri almak için aynı kutuyu tekrar işaretle.

**Geri alma:** Geri Al'a bas, IPv6 tekrar açılır.

## Fare ivmesini kapat

`fix.input.mouse_accel_off`

'İşaretçi duyarlılığını artır' (Enhance pointer precision), fareyi hızlı oynattığında imlecin daha uzağa gitmesine neden olur. Nişancı oyunlarında bu, nişanı süzülen ve tutarsız hissettirir. Bunu kapatacağız.

**Yeniden başlatma gerekir**

1. Win+R'ye bas, "main.cpl" yaz ve Enter'a basarak Fare Özellikleri'ni aç.
2. "İşaretçi Seçenekleri" sekmesine geç.
3. "İşaretçi duyarlığını artır" kutusunun işaretini kaldır ve "Tamam"a bas.
4. Bilgisayarı yeniden başlat, sonra Atış Poligonu'nda nişanını dene.

**Geri alma:** Geri Al'a bas, eski fare ayarı geri gelir.

## İkinci ağ bağlantısını kapat

`fix.net.disable_second_adapter`

İki ağ bağlantısı aynı anda açık (mesela kablo ve Wi-Fi). Windows maçın ortasında aralarında geçiş yapabilir; bu lag ve kopmalara yol açar. Kullanmadığın bağlantıyı kapatacağız.

**Yönetici izni gerekir**

1. Başlat düğmesine sağ tıkla ve "Aygıt Yöneticisi"ni aç.
2. "Ağ bağdaştırıcıları"nı aç ve VTDiag raporunda adı geçen bağdaştırıcıyı bul.
3. Ona sağ tıkla, "Cihazı devre dışı bırak"ı seç ve "Evet"e bas.
4. İnternetin hâlâ çalıştığını kontrol et; geri almak için sağ tıklayıp "Cihazı etkinleştir"i seç.

**Geri alma:** VTDiag'in geri alma listesinde Geri Al'a bas, bağdaştırıcı tekrar açılır.

## Kullanmadığın ağ portunu kapat

`fix.net.disable_unused_adapter`

Bilgisayarında açık ama kullanılmayan kablolu bir Realtek ağ portu var. Bu sürücünün VALORANT'ta birkaç saniyede bir kısa donmalara yol açtığı biliniyor. Onu kapatacağız; şu anki bağlantın çalışmaya devam eder.

**Yönetici izni gerekir**

1. Başlat düğmesine sağ tıkla ve "Aygıt Yöneticisi"ni aç.
2. "Ağ bağdaştırıcıları"nı aç ve VTDiag raporunda adı geçen Realtek bağdaştırıcısını bul.
3. Ona sağ tıkla, "Cihazı devre dışı bırak"ı seç ve "Evet"e bas.
4. İnternetin hâlâ çalıştığını kontrol et; geri almak için sağ tıklayıp "Cihazı etkinleştir"i seç.

**Geri alma:** VTDiag'in geri alma listesini aç ve Geri Al'a bas. Port hemen tekrar açılır.

## DNS önbelleğini temizle

`fix.net.dns_flush`

Windows, daha önce baktığı internet adreslerinin bir listesini tutar. Bir kayıt eskiyse ya da yanlışsa Riot sunucularına ulaşılamaz. Listeyi temizlemek zararsızdır; Windows listeyi kendisi yeniden oluşturur.

**Yönetici izni gerekir**

1. Başlat düğmesine sağ tıkla ve "Terminal (Yönetici)"yi aç (Windows 10'da: "Windows PowerShell (Yönetici)").
2. "ipconfig /flushdns" yaz ve Enter'a bas.
3. DNS önbelleğinin temizlendiği mesajını bekle, sonra Riot Client'ı aç.

**Geri alma:** Geri alınacak bir şey yok: Windows listeyi otomatik doldurur.

## Ağ kartındaki güç tasarrufunu kapat

`fix.net.realtek_power_saving_off`

Ağ kartında 'Energy Efficient Ethernet' ya da 'Green Ethernet' açık. Bu güç tasarrufu özellikleri bağlantının bir an durmasına yol açabilir. Bunları kapatacağız.

**Yönetici izni gerekir**

1. Başlat düğmesine sağ tıkla ve "Aygıt Yöneticisi"ni aç.
2. "Ağ bağdaştırıcıları"nı aç, VTDiag raporunda adı geçen bağdaştırıcıya sağ tıkla ve "Özellikler"i seç.
3. "Gelişmiş" sekmesine geç.
4. Bunlardan hangisini görürsen "Disabled" (Devre Dışı) yap: "Energy-Efficient Ethernet", "Green Ethernet", "Advanced EEE", "Gigabit Lite".
5. "Tamam"a bas; bağlantın birkaç saniye kopup geri gelebilir.

**Geri alma:** Geri Al'a bas, eski güç tasarrufu ayarları geri gelir.

## Bağlantındaki IPv6'yı kapat

`fix.net.unbind_ipv6_active`

Bazı ağ kartları (mesela Killer) ve bazı internet sağlayıcılar IPv6 ile sorun yaşar; bu sesli sohbeti bozabilir ya da VAL 57 / 59 hatalarına yol açabilir. IPv6'yı sadece kullandığın bağlantıda kapatacağız. Normal internet IPv4 üzerinden çalışmaya devam eder.

**Yönetici izni gerekir**

1. Win+R'ye bas, "ncpa.cpl" yaz ve Enter'a basarak Ağ Bağlantıları'nı aç.
2. Kullandığın bağlantıya (VTDiag raporunda adı geçen) sağ tıkla ve "Özellikler"i seç.
3. "İnternet Protokolü Sürüm 6 (TCP/IPv6)" kutusunun işaretini kaldır ve "Tamam"a bas.
4. Sesli sohbetle bir maç oyna; geri almak için aynı kutuyu tekrar işaretle.

**Geri alma:** Geri Al'a bas, IPv6 tekrar açılır.

## Windows ağ ayarlarını sıfırla (Winsock)

`fix.net.winsock_reset`

VPN'lerden, hızlandırıcılardan ya da eski yazılımlardan gelen ek ağ filtreleri Windows'un Riot ile konuşmasını bozabilir. Winsock sıfırlaması ağ ayarlarını fabrika haline döndürür. Bu tek tıkla geri alınamaz ve yeniden başlatma gerekir.

**Yönetici izni gerekir** · **Yeniden başlatma gerekir**

1. Başlat düğmesine sağ tıkla ve "Terminal (Yönetici)"yi aç (Windows 10'da: "Windows PowerShell (Yönetici)").
2. "netsh winsock reset" yaz ve Enter'a bas.
3. İstediğinde bilgisayarı yeniden başlat.
4. Sonrasında bir VPN ya da ağ aracı çalışmazsa onu yeniden yükle.

**Geri alma:** Bu tek tıkla geri alınamaz. Bir şey çalışmazsa, eski ayarlara ihtiyaç duyan VPN'i ya da ağ aracını yeniden kur.

## Hızlı başlatmayı kapat

`fix.os.fast_startup_off`

Hızlı başlatma açıkken 'Kapat' Windows'u tam olarak yeniden başlatmaz; bu yüzden Vanguard temiz yüklenemez ve eski açılış dosyaları kalır. Hızlı başlatmayı kapatacağız, böylece her kapatma temiz bir başlangıç olur.

**Yönetici izni gerekir** · **Yeniden başlatma gerekir**

1. Win+R'ye bas, "powercfg.cpl" yaz ve Enter'a basarak Güç Seçenekleri'ni aç.
2. Soldaki "Güç düğmelerinin yapacaklarını seçin"e tıkla.
3. "Şu anda kullanılamayan ayarları değiştirin"e tıkla.
4. "Hızlı başlatmayı aç (önerilir)" kutusunun işaretini kaldır ve "Değişiklikleri kaydet"e bas.
5. Bilgisayarı bir kez yeniden başlat.

**Geri alma:** Geri Al'a bas, Hızlı başlatma tekrar açılır.

## Bilgisayar saatini düzelt

`fix.os.time_resync`

Bilgisayarının saati yanlış. Yanlış saat güvenli bağlantıları bozar; bu yüzden Riot Client giriş yapamaz ya da Vanguard bağlanamaz. Saati Microsoft'un zaman sunucusuyla eşitleyeceğiz.

**Yönetici izni gerekir**

1. Ayarlar > Saat ve dil > Tarih ve saat'i aç.
2. "Saati otomatik olarak ayarla" açık olsun, sonra "Şimdi eşitle"ye bas.
3. Ya da "Terminal (Yönetici)"yi aç, "w32tm /resync" yaz ve Enter'a bas.
4. Saatin doğru olduğunu kontrol et, sonra Riot Client'ı aç.

**Geri alma:** Geri Al, saati yanlış zamana geri çevirmez (bu her şeyi yine bozardı).

## İşlemcinin tam hızda çalışmasına izin ver

`fix.power.cpu_max_100`

'Maksimum işlemci durumu' güç ayarı %100'ün altında, bu yüzden işlemcin tam hıza hiç çıkmıyor. Bunu tekrar %100'e getireceğiz.

**Yönetici izni gerekir**

1. Win+R'ye bas, "powercfg.cpl" yaz ve Enter'a basarak Güç Seçenekleri'ni aç.
2. Kullandığın planın yanındaki "Plan ayarlarını değiştir"e, sonra "Gelişmiş güç ayarlarını değiştir"e tıkla.
3. "İşlemci güç yönetimi" > "En yüksek işlemci durumu"nu aç.
4. "Prize takılı" (masaüstünde "Ayar") değerini %100 yap ve "Tamam"a bas.

**Geri alma:** Geri Al'a bas, eski sınır geri gelir.

## Yüksek performans güç planına geç

`fix.power.high_performance`

Bilgisayarın, işlemciyi yavaşlatarak enerji tasarrufu yapan bir güç planı kullanıyor. VALORANT tam hız alsın diye Yüksek performans planına geçeceğiz.

**Yönetici izni gerekir**

1. Başlat düğmesine sağ tıkla ve "Terminal (Yönetici)"yi aç (Windows 10'da: "Windows PowerShell (Yönetici)").
2. "powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" yaz ve Enter'a bas.
3. Win+R'ye bas, "powercfg.cpl" yaz ve "Yüksek performans"ın seçili olduğunu kontrol et.
4. Geri almak için aynı pencerede eski planını seç.

**Geri alma:** Geri Al'a bas, eski güç planın geri gelir.

## Windows'un USB portlarını uyutmasını engelle

`fix.power.usb_selective_suspend_off`

Windows, güç tasarrufu için USB portlarını uyutabilir. Bazı bilgisayarlarda bu, fare, klavye ya da kulaklığın bir an kopmasına veya bütün bilgisayarın donmasına yol açar. Bunu kapatacağız.

**Yönetici izni gerekir**

1. Win+R'ye bas, "powercfg.cpl" yaz ve Enter'a basarak Güç Seçenekleri'ni aç.
2. Kullandığın planın yanındaki "Plan ayarlarını değiştir"e, sonra "Gelişmiş güç ayarlarını değiştir"e tıkla.
3. "USB ayarları" > "USB seçici askıya alma ayarı"nı aç.
4. Bunu "Devre Dışı" yap (iki satır görürsen hem "Pilde" hem "Prize takılı") ve "Tamam"a bas.

**Geri alma:** Geri Al'a bas, eski güç ayarı geri gelir.

## Overlay ve kayıt programlarını bu oturum için kapat

`fix.proc.close_overlays`

Overlay programları (RivaTuner, Overwolf ya da klip kaydedicileri gibi) ekrana çizim yapmak için oyuna bağlanır. Son VALORANT güncellemelerinden sonra bu bağlantılar takılmaya, donmaya ve yüklemede takılı kalmaya yol açıyor. Şimdilik kapatacağız; bilgisayarı bir sonraki açışında yine başlarlar.

1. VTDiag raporunda adı geçen her overlay ya da kayıt programını bul.
2. Penceresini X düğmesiyle kapat.
3. Saatin yanında hâlâ çalışıyorsa simgesine sağ tıkla ve "Çıkış" ya da "Kapat"ı seç.
4. VALORANT'ı aç ve bir maç oyna; bu programlar bilgisayarı bir dahaki açışında yine başlar.

**Geri alma:** Geri alınacak bir şey yok: programları istediğin zaman tekrar açabilirsin.

## Riot Client'ı her zaman yönetici olarak çalıştır

`fix.riot.client_runasadmin`

Birçok oyuncu, güncellemelerden sonra yaşadığı donma ve siyah ekran sorunlarını Riot Client'ı (RiotClientServices.exe) yönetici olarak çalıştırarak çözdü. Bu seçeneği sadece Riot Client'a açacağız, oyuna değil.

**Yönetici izni gerekir**

1. VALORANT'ı ve Riot Client'ı kapat.
2. VTDiag raporunda yazan RiotClientServices.exe dosyasının klasörünü aç.
3. RiotClientServices.exe'ye sağ tıkla, "Özellikler"i seç ve "Uyumluluk" sekmesine geç.
4. "Bu programı yönetici olarak çalıştır" kutusunu işaretle ve "Tamam"a bas ("Tüm kullanıcılar için ayarları değiştir"i kullanma).
5. Riot Client'ı yeniden aç ve Windows sorduğunda "Evet" de.

**Geri alma:** Geri Al'a bas, seçenek kaldırılır.

## Riot programlarından zorla CPU önceliğini kaldır

`fix.sec.remove_riot_perfoptions`

VALORANT ya da Vanguard zorla 'Yüksek' veya 'Gerçek zamanlı' öncelikte çalıştırılıyor. Bu, Vanguard'ı ve Windows'u aç bırakır ve VAN 136 / VAN 185 atılmalarına yol açabilir. Zorla öncelikleri kaldıracağız, böylece Windows bunu normal şekilde yönetir.

**Yönetici izni gerekir**

1. Win+R'ye bas, "regedit" yaz ve Enter'a basarak Kayıt Defteri Düzenleyicisi'ni aç.
2. "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" altında VTDiag'ın yazdığı programa, sonra "PerfOptions"a git.
3. "PerfOptions" anahtarına sağ tıkla ve "Dışa Aktar" ile yedeğini al.
4. Sağ tarafta sadece "CpuPriorityClass" ve "IoPriority" değerlerini sil (anahtarın kendisini silme).
5. Rapordaki her program için aynısını yap, sonra bilgisayarı yeniden başlat.

**Geri alma:** Geri Al'a bas, zorla öncelik geri gelir.

## Riot programlarından Exploit Protection geçersiz kılmalarını kaldır

`fix.sec.reset_exploit_overrides`

Birisi (ya da bir ayar aracı) Riot veya Vanguard programları için Windows Exploit Protection ayarlarını kapatmış. Vanguard bu korumalara ihtiyaç duyar ve onlar olmadan VAN 9002 gösterir. Geçersiz kılmaları kaldıracağız, böylece Windows varsayılanları yeniden geçerli olur.

**Yönetici izni gerekir**

1. Windows Güvenliği > Uygulama ve tarayıcı denetimi > Açıklardan yararlanma koruması ayarları'nı aç.
2. "Program ayarları" sekmesine geç.
3. VTDiag raporunda adı geçen programa (örneğin vgc.exe) tıkla ve "Kaldır"a bas.
4. Rapordaki her program için aynısını yap, sonra bilgisayarı yeniden başlat.

**Geri alma:** Geri Al'a bas, eski geçersiz kılmalar geri gelir.

## VALORANT ile çakışan programı kapat

`fix.svc.disable_conflict_service`

Bilgisayarındaki bir arka plan programının VALORANT ya da Vanguard ile çakıştığı biliniyor. Onu durduracağız ve Windows ile başlamasını engelleyeceğiz. Program kurulu kalır.

**Yönetici izni gerekir**

1. Win+R'ye bas, "services.msc" yaz ve Enter'a basarak Hizmetler'i aç.
2. VTDiag raporunda adı geçen hizmete çift tıkla ("Hizmet adı"nın aynı olduğunu kontrol et).
3. "Başlangıç türü"nü "Devre Dışı" yap ve "Uygula"ya bas.
4. "Durdur"a, sonra "Tamam"a bas.
5. Geri almak için "Başlangıç türü"nü eski haline getir.

**Geri alma:** Geri Al'a bas, program yine Windows ile başlar.

## Vanguard'ın açılış durumu dosyasını sıfırla

`fix.vg.quarantine_bootstatus`

Vanguard, doğru başlayıp başlamadığını hatırlayan küçük bir dosya tutar (vgkbootstatus.dat). Bu dosya takılı kalırsa Vanguard sürekli yeniden başlatma ister. Dosyayı VTDiag'in karantina klasörüne taşıyacağız. Sonra sen yeniden başlatırsın.

**Yönetici izni gerekir** · **Yeniden başlatma gerekir**

1. VALORANT'ı ve Riot Client'ı kapat, saatin yanındaki simgesi de dahil.
2. Masaüstünde yeni bir klasör oluştur, örneğin "Vanguard yedek".
3. VTDiag'ın vgkbootstatus.dat dosyasını bulduğu klasörü aç (yol raporunda yazıyor).
4. Dosyayı Kes ve Yapıştır ile masaüstündeki klasöre taşı, Windows sorarsa "Devam"a bas (silme).
5. Bilgisayarı yeniden başlat, sonra VALORANT'ı aç.

**Geri alma:** Geri Al'a bas, dosya yerine konur.

## Eski bir Vanguard açılış dosyasını yoldan çek

`fix.vg.quarantine_stale_dat`

Eski bir Vanguard açılış dosyası (392667600.dat) geride kalmış; bu mavi ekranlara ya da 'yeniden başlatma gerekli' döngülerine yol açabilir. Dosyayı VTDiag'in güvenli karantina klasörüne taşıyacağız (silmeyeceğiz). Sonra sen yeniden başlatırsın.

**Yönetici izni gerekir** · **Yeniden başlatma gerekir**

1. VALORANT'ı ve Riot Client'ı kapat, saatin yanındaki simgesi de dahil.
2. Masaüstünde yeni bir klasör oluştur, örneğin "Vanguard yedek".
3. VTDiag'ın 392667600.dat dosyasını bulduğu klasörü aç (yol raporunda yazıyor).
4. Dosyayı Kes ve Yapıştır ile masaüstündeki klasöre taşı, Windows sorarsa "Devam"a bas (silme).
5. Bilgisayarı yeniden başlat, sonra VALORANT'ı aç.

**Geri alma:** Geri Al'a bas, dosya eski yerine konur.

## Vanguard'ın Windows ile başlamasını sağla

`fix.vg.vgc_automatic`

Vanguard'ın hizmeti (vgc) sadece istenince başlayacak şekilde ayarlı ve bilgisayarında düzgün başlamıyor. Riot'un kendi çözümü onu otomatik başlayacak şekilde ayarlamak. Bunu sadece Vanguard Pre-Check'in kapalı olduğunu söylediğin için öneriyoruz.

**Yönetici izni gerekir** · **Yeniden başlatma gerekir**

1. Win+R'ye bas, "services.msc" yaz ve Enter'a basarak Hizmetler'i aç.
2. "vgc" hizmetine çift tıkla.
3. "Başlangıç türü"nü "Otomatik" yap ve "Uygula"ya bas.
4. "Başlat"a, sonra "Tamam"a bas.
5. Bilgisayarı yeniden başlat, sonra VALORANT'ı aç.

**Geri alma:** Geri Al'a bas, hizmet eski başlatma ayarına döner.
