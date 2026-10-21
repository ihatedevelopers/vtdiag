# Turn on Secure Boot (VAN 9003)

Keep this page open on your phone while your PC is in the BIOS. VTDiag continues by itself after you sign in again.

> VTDiag is not affiliated with Riot Games. Changing BIOS settings is a step only you can do; VTDiag never changes
> firmware settings for you.

## Before you start

- **BitLocker:** if your drive is encrypted, save your recovery key first: <https://aka.ms/myrecoverykey>. After a BIOS
  change Windows may ask for it.
- **Disk style:** Secure Boot needs a GPT system disk and UEFI mode. VTDiag's guide tells you if your disk is still MBR;
  that has to be fixed first (Microsoft's `mbr2gpt` tool, ideally with a helper).

## Enable Secure Boot {#enable}

1. Restart into the BIOS. In VTDiag, click **Restart into BIOS**, or press the BIOS key while the PC starts
   (usually **Del** or **F2**).
2. Find the Secure Boot setting. Common places:
   - ASUS: Advanced Mode (F7) > Boot > Secure Boot > OS Type: **Windows UEFI mode**
   - MSI: Settings > Security > Secure Boot > **Enabled**
   - Gigabyte: Boot > Secure Boot > **Enabled** (turn **CSM Support** off first)
   - ASRock: Security > Secure Boot > **Enabled**
3. If Secure Boot is greyed out, turn **CSM / Legacy boot** off first (Boot menu), save, restart into the BIOS again.
4. Choose **Save & Exit**. Windows starts normally.

## Secure Boot is on but VALORANT still says VAN 9003 {#setup-mode}

Your BIOS may be in **Setup Mode** (no keys installed). In the Secure Boot menu choose **Restore Factory Keys** or
**Install default Secure Boot keys**, then Save & Exit.

## After the restart

VTDiag opens by itself and checks Secure Boot again. If it's still off, it suggests the next thing to try.

---

## Türkçe: Secure Boot'u aç (VAN 9003)

Bilgisayar BIOS'tayken bu sayfayı telefonunda açık tut. Oturum açtıktan sonra VTDiag kendiliğinden devam eder.

1. **BitLocker** açıksa önce kurtarma anahtarını kaydet: <https://aka.ms/myrecoverykey>.
2. VTDiag'da **BIOS'a yeniden başlat**'a tıkla ya da bilgisayar açılırken BIOS tuşuna bas (genelde **Del** veya **F2**).
3. Secure Boot ayarını bul (ASUS: Advanced Mode > Boot > Secure Boot; MSI: Settings > Security > Secure Boot) ve
   **Enabled** yap. Gri görünüyorsa önce **CSM / Legacy** önyüklemeyi kapat.
4. **Save & Exit** ile kaydet ve çık.
5. Secure Boot açık ama hata sürüyorsa: Secure Boot menüsünde **Restore Factory Keys** seç, kaydet ve çık.
