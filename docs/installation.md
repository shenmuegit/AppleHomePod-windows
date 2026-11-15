# Install AppleHome 0.2.0 Preview 5

Verified on Windows 10 Pro 22H2 (build 19045, x64) with one standalone HomePod mini running software 27.2. There is no model or firmware playback allowlist, but Windows 11, other HomePod combinations and stereo pairs have not been validated on hardware. This is an unsigned prerelease.

## First setup

1. Set up HomePod in Apple's Home app. Connect the PC and HomePod to the same local network; Wi-Fi or Ethernet is suitable for the PC.
2. Download `AppleHome-0.2.0-6fc6482b1521.zip` from the [Preview 5 release](https://github.com/shenmuegit/AppleHomePod-windows/releases/tag/v0.2.0-preview.5), extract it, and open `AppleHome.exe`. No separate .NET or Rust installation is needed.
3. Follow the wizard. Allow the audio driver installation when Windows asks. If Windows requires a restart, save your work, restart, and open the app again.
4. Wait for HomePod to connect, then confirm that you hear audio. One discovered HomePod with valid connection details connects automatically; if several appear, choose the device. The wizard names and selects the Windows output and handles timing parameters.

The wizard follows the Windows preferred display language: English, Simplified Chinese or Traditional Chinese. Other preferences fall back to English. There is no language, network-adapter or buffer-size setting to fill in.

A full first-install run, including automatic driver installation and the restart, remains unvalidated for this build. The accepted repair was checked by updating an installation with a valid driver, device binding and calibration. Archives retain the documentation from their source snapshot; this guide and the release notes contain the current instructions and validation status.

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

Music may pause briefly. The update retains the existing binding and calibration and only replaces application-owned files and startup tasks. If ownership, binding or calibration validation fails, resolve that error before retrying. Automatic updates are not available.

After updating, use normal music to check playback, Windows volume changes, and recovery after mute. The full compatibility record is in the [compatibility matrix](https://github.com/shenmuegit/AppleHomePod-windows/blob/main/docs/COMPATIBILITY.md).

## Verify the download

Download `SHA256SUMS.txt` from the same release and compare its entry with:

```powershell
Get-FileHash .\AppleHome-0.2.0-6fc6482b1521.zip -Algorithm SHA256
```

The matching source archive is `AppleHome-source-6fc6482b1521.zip`. The release tag points to binary source commit `6fc6482b152188f29e37a9a7d55f8502ebec29ad`. The executable's SHA256 is `434abefdc0ec75646a4e30ab6cfbcbd100ecd73cf318bee9d7380a7627e0cfd0`.

## Playback and recovery validation

Preview 5 removes model and firmware checks that could reject a discoverable HomePod before its protocol handshake. Identity, connection details and authentication still have to be valid. Timing preferences and saved buffer records guide the connection without becoming permission to play; a firmware change invalidates the previous profile's timing reference.

The listener confirmed continuous normal music, Windows volume changes and recovery after at least 15 seconds of mute on the mini running 27.2. Its session used a negotiated 250 ms buffer candidate; users do not enter this value, and it is not a universal default or an acoustic latency measurement.

The release also includes persistent engine supervision while the HomePod output is selected and the earlier automatic mDNS failover. Silence, pause, mute and accumulated failures do not cancel the selected output's running intent. Switching to another Windows output releases the sender.

**Overnight idle followed by playback and real sleep/wake recovery have not been tested on hardware for this release.** Extended music/video runs, physical network-outage recovery and other Home access/PIN policies remain outside this build's hardware acceptance. See the release notes and compatibility record for the per-build evidence.

## Driver and license

The application changes are GPLv3 or later. The bundled, unmodified VB-CABLE Pack45 driver is separate software by VB-Audio Software under its own [Donationware terms](https://vb-audio.com/Services/licensing.htm). Its original archive and vendor readme are included; the app's About view provides the license and donation link. VB-CABLE is not covered by the application's GPL license.
