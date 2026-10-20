# AppleHome

<!-- impeccable:product-schema 1 -->

## Platform

Windows native desktop (WPF / .NET). This is not a web page or a mobile application.

## Users

Windows 10/11 users who want computer music and video audio on their HomePod without configuring AirPlay protocols.

## Product Purpose

Add a dedicated HomePod output to Windows. Setup installs the necessary verified driver, discovers a receiver, configures the output and confirms audible playback. Daily operation uses Windows output selection, volume and mute.

## Operating Context

The PC and HomePod are on the same local network. The currently qualified hardware is one HomePod mini running 27.0 on Windows10 build19045 x64. Windows11 and other receivers remain unqualified. The working installed backend is preserved while new setup candidates are developed.

## Capabilities and Constraints

- App name: AppleHome; GPLv3+ application, using an unmodified VB-CABLE package with its vendor notices and donationware information.
- Installation automatically names and selects the verified ordinary playback endpoint; users never enter buffer milliseconds.
- The confirmation screen offers playback/replay and explicit heard/not-heard feedback. Replaying is a listening action, not a completed installation or an adaptation attempt.
- Native setup reads Windows preferred UI languages at startup, reusing English, Simplified Chinese and Traditional Chinese resources. There is no setup language selector; unmatched preferences fall back to English. Device names remain unchanged.
- The user specifically reports a long wait between pressing replay and first sound. Preserve the finite confirmation clip; reduce the work on that click path.
- Daily use remains Windows native controls. No microphone recording, physical-speaker fallback, or network-disconnection tests in this work.
- Project worktrees, caches, reports, builds and deliveries stay within the owning project. The application itself uses its normal Windows user install/config paths; a developer's local workspace path is not a product dependency.

## Brand Commitments

Keep setup minimal, clear and polished. The previous bare text-and-buttons design was explicitly rejected; its visual arrangement is not authority. Preserve the native Windows title bar, keyboard navigation and accessible state feedback.

The user explicitly prefers Apple Devices for Windows as the visual reference: clear device identity, quiet neutral layers, precise typography and restrained blue actions. Preserve native Windows window controls and the single setup task. Neither Apple Devices nor PowerToys is an application dependency.

## Evidence on Hand

README.md and docs/validation/windows-native-homepod-output.md distinguish historical music/video hardware results from software checks and unqualified clean-install work. Do not generalize to arbitrary HomePod models, operating systems, acoustic latency or installation environments.

## Product Principles

- Prepare connection and audio resources before asking the user to listen.
- Respond immediately to a playback action and show truthful preparation/playing/ready states.
- Keep only information that helps the current decision; retain actionable failures and licensing access.
- Name the user's speaker HomePod in setup; keep protocol terms and raw discovery errors in diagnostics, and offer only recovery actions the interface provides.
- Confirm audibility explicitly before saving an audible reference or completing setup.
