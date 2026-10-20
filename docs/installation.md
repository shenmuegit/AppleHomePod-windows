# Install AppleHome 0.2.0 Preview 2

Validated on Windows 10 Pro 22H2 (build 19045, x64) with one HomePod mini running software 27.0. Windows 11, other HomePod models or firmware, and stereo pairs have not been validated. This is an unsigned prerelease.

## First setup

1. Set up HomePod in Apple's Home app. Connect the PC and HomePod to the same local network; Wi-Fi or Ethernet is suitable for the PC.
2. Download `AppleHome-0.2.0-ec4a0e797fed.zip` from the [Preview 2 release](https://github.com/shenmuegit/AppleHomePod-windows/releases/tag/v0.2.0-preview.2), extract it, and open `AppleHome.exe`. No separate .NET or Rust installation is needed.
3. Follow the wizard. Allow the audio driver installation when Windows asks. If Windows requires a restart, save your work, restart, and open the app again.
4. Wait for HomePod to connect, then confirm that you hear audio. One eligible HomePod connects automatically; if several appear, choose the device. The wizard names and selects the Windows output and handles timing parameters.

The wizard currently uses Simplified Chinese:

| Button | Meaning |
|---|---|
| `再播放一次` | Replay |
| `听到了` | I heard it — finish setup |
| `没听到` | No audio — check and retry |

A full first-install run, including automatic driver installation and the restart, remains unvalidated for this release. The accepted binary was tested by updating an existing qualified installation. Archives retain the documentation from their source snapshot; this guide and the release notes contain the current instructions and validation status.

## Everyday playback

Play normal music or video from your usual Windows apps. Use Windows volume and mute controls. Switch between HomePod, headphones and speakers through the Windows output menu. The background app starts at sign-in; no separate daily control panel is required.

## Update an existing preview

Reopening the setup wizard reuses the installed application; it does not replace its executable. For an installation with a valid driver, device binding and calibration, open PowerShell in the newly extracted folder and run:

```powershell
$stop = Start-Process .\AppleHome.exe -ArgumentList '--native-disable' -Wait -PassThru
if ($stop.ExitCode -ne 0) { throw 'AppleHome could not stop the existing background app.' }
$update = Start-Process .\AppleHome.exe -ArgumentList '--native-enable' -Wait -PassThru
if ($update.ExitCode -ne 0) { throw 'AppleHome could not update and enable the background app.' }
```

Music may pause briefly. The update retains the existing qualified device record and only replaces application-owned files and startup tasks. If an ownership or qualification check fails, resolve that error before retrying. Automatic updates are not available.

After updating, use normal music to check playback, Windows volume changes, and recovery after mute. The full compatibility record is in the [compatibility matrix](https://github.com/shenmuegit/AppleHomePod-windows/blob/main/docs/COMPATIBILITY.md).

## Verify the download

Download `SHA256SUMS.txt` from the same release and compare its entry with:

```powershell
Get-FileHash .\AppleHome-0.2.0-ec4a0e797fed.zip -Algorithm SHA256
```

The matching source archive is `AppleHome-source-ec4a0e797fed.zip`. Both archives are preserved from tested source commit `ec4a0e797fedcc7110f29902c1042aab98830feb`. The executable's SHA256 is `16820bdf006b21779dbbf30f1acda64f451a411ba5857a21b31c5b2c0730ce82`.

## Driver and license

The application changes are GPLv3 or later. The bundled, unmodified VB-CABLE Pack45 driver is separate software by VB-Audio Software under its own [Donationware terms](https://vb-audio.com/Services/licensing.htm). Its original archive and vendor readme are included; the app's About view provides the license and donation link. VB-CABLE is not covered by the application's GPL license.
