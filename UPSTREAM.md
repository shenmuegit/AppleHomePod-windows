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
