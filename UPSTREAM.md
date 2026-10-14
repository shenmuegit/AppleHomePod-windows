# AppleHome source and build provenance

AppleHome is a GPLv3+ derivative of [AirFlash](https://github.com/Ding-Kyoma/AirFlash).
Original copyright, source notices, `LICENSE`, `LICENSE-GPLv3`, and third-party
notices are preserved. This fork's changes use GPLv3+; no commercial license
agreement is claimed or supplied for AppleHome changes.

- Upstream commit: `cf420d97700463c56adc31408510e8336e99e51c`.
- Archive: `https://codeload.github.com/Ding-Kyoma/AirFlash/zip/cf420d97700463c56adc31408510e8336e99e51c`.
- Archive SHA256: `f1110808957de47f1c0e7d550b46341206d9d9e59ede73c4b0a496af2f7fe645`.
- Import date: 2026-10-14. Archive hash, destination boundaries, and collisions were checked.
- Existing AppleHome research/specification documents were retained.
- Upstream GitHub publication workflows are not shipped. The single Python
  source-order assertion for the omitted release workflow is removed; runtime,
  SDK-preflight, and packaging checks remain.
- .NET SDK 10.0.402 changed the implicit ILLink package from 10.0.12 to 10.0.13.
  The lock was regenerated and locked restore passed. Explicit application
  dependency versions, including NAudio 2.2.1, are unchanged.

## Baseline validation

Observed on Windows 10 Pro x64 build 19045:

- .NET 10.0.402: locked restore and 202 tests passed.
- Rust/Cargo 1.99.0: 49 tests passed, 2 hardware/long-soak tests intentionally ignored.
- Clippy 0.1.99 with warnings denied: passed; official component SHA256 verified.
- Python validation environment 3.12.0, uv 0.13.1: 95 tests passed; Ruff passed.
- Native release build and finite JSONL v1 hello: passed.
- Native compatibility audit: passed its configured checks; 144 imports remain
  unconfirmed by the audit catalog. Static audit is not hardware qualification.

Build tools and caches are local/ignored or reused from an existing toolchain.
The desktop deliverable is self-contained; those developer tools are not
runtime requirements. No baseline test sent audio to a HomePod.

## AppleHome adaptation and diagnostic status

AppleHome now uses its own data/cache/credentials/tray/single-instance identity,
requires an explicit user action before capture, bounds JSONL reads while reading,
validates manual endpoints and persisted settings, protects logs/exports, and
journals only application-owned local mute changes. Recovery is explicit on normal
startup and never runs from the read-only audio diagnostic.

The native peer schema retains an additive optional `follow_receiver_clock`
boolean, default false, for explicit diagnostics. The normal application no longer
enables it based on OS 27 metadata. PTP receiver-clock parsing and anchor formats
have regression coverage; packet SSRC is zero on PTP, but those changes remain
acoustically unqualified. Actual mini playback succeeded using the existing native
NTP/ALAC path with a 2-second target; a 200 ms target remained inaudible. Windows
selects that profile only for the discovered standalone AudioAccessory5,1 on 27.0,
retaining the original path for other combinations. No second engine is integrated.

Final review fixes restrict capture to playback endpoints, preserve external mute
choices across reconnect and persistence failures, reject invalid manual IDs,
redact secret-label variants and free-form exported details, and reject stale
discovery snapshots after interface changes. Each finding has RED-to-GREEN coverage.

Current software checks: .NET 270 passed, Rust 53 passed / 2 explicitly ignored,
Python 95 passed, Clippy and Ruff passed, UI smoke 577 checks passed. A separate
real Windows endpoint harness passed 8 mute ownership/recovery checks without
capturing audio or changing the volume scalar; the locked-journal failure path
also passed after reproducing the original fault. Unsaved journal clears remain
ambiguous across a crash, are surfaced, and are retried before closing.
See docs/VALIDATION.md for the inaudible Windows trials and the audible iPhone baseline.

Independent Node and GPL-2.0 Rust senders are ignored diagnostic checkouts and
are not runtime or source dependencies of the shipped application. The latter
produced an audible finite WAV on the actual mini with NTP/ALAC; the former did
not. This does not qualify AppleHome's WASAPI path. The portable application
remains the WPF/Rust GPLv3+ build described here.
