# Native volume and capture latency implementation plan

> **For agentic workers:** Execute the approved research follow-up with superpowers:executing-plans; use the existing Rust worker only for its disjoint capture file. Steps use checkbox syntax.

**Goal:** Reduce Windows master-volume/mute response delay and capture backlog on the bound mini without a daily app panel.

**Architecture:** Reuse the existing verified receiver volume command, sequence/readback state, native monitor and Rust capture queue. On the calibrated MasterInPcm=false driver, use receiver dB for master attenuation; keep only integer-step/floor residual attenuation in PCM. Keep the post-master driver path unchanged. Preserve audio during initialization; reclaim exact digital silence and regulate the open capture queue with bounded resampling.

**Spec:** Existing [native output design](../specs/2026-10-14-windows-native-homepod-output-design.md) with this user-authorized amendment. Research: [community review](../../research/2026-10-18-homepod-windows-latency-community-review.md).

## Constraints and design ruling

- User explicitly authorized this implementation on 2026-10-18. Keep Windows output/volume/mute as the only daily controls; no new dependencies or options.
- Device volume cannot exceed measured B=72 on the current binding. Map Windows dB relative to calibrated MaxDb, never treat its scalar as linear amplitude. Integer receiver steps are 0.3 dB; use PCM residual only for quantization and the nonzero receiver floor.
- Remote mute/zero is command 0. Keep the source flowing at the last residual gain after confirmed device mute, so unmute does not wait behind newly inserted PCM zeroes. Missing/failed readback still holds/stops safely; explicit external zero without newer Windows intent still mutes the virtual endpoint.
- A newer Windows revision supersedes a pending volume target. Do not wait for an old target before sending the latest one. Gate remains closed until the initial matching readback; no old epoch may open a replacement route.
- Post-master capture retains its existing PCM gain/mute path. Per-app attenuation remains Windows-owned; avoid applying master attenuation twice.
- Capture gate closed retains all first audio and the 2000 ms overflow bound. Open queue may remove only exact leading digital-zero frames down to 20 ms. Native resample feedback is bounded to ±0.5%; legacy remains ±0.05%. Continuous nonzero startup audio drains gradually, not instantly.
- No changes to RTP/NTP anchors, authentication, firmware guards, drivers, firewall or output names. Physical disconnect is deferred. Acoustic latency remains unqualified until user observation/measurement.

## Task 1: Receiver master-volume path

**Files:** PcmVolumeCalibration.cs, NativeOutputController.cs, SessionController.cs; corresponding .NET volume/controller tests and the existing WireConnection test double.

- [x] RED: literal dB mapping and floor residual cases; invalid inputs; Windows master/mute goes to device, latest pending target wins, no double PCM attenuation, initial gate waits for readback, reconnect/external zero remain safe.
- [x] Implement one shared mapping helper and the MasterInPcm=false controller branch using existing commands/readbacks. Native device writes use 50 ms debounce; legacy retains 200 ms.
- [x] GREEN: full .NET suite, including existing post-master, stale generation/epoch and mute restoration tests; commit verified change.

## Task 2: Capture backlog

**Files:** native/airflash-engine/src/live.rs and its deterministic tests.

- [x] RED: closed gate preserves first audio; open silence trimming retains all nonzero audio; resample feedback drains a nonzero backlog while preserving ordering; reset/overflow and legacy bounds remain.
- [x] Implement silence reclaim and bounded correction using existing rubato/resampler, with honest queue-age and dropped-frame metrics.
- [x] GREEN: full cargo test, clippy and static release build; no network/hardware side effects; commit verified change.

## Task 3: Candidate and actual trial

- [x] Run one targeted fresh review for the combined control/queue changes; fix material findings with regression evidence.
- [x] Publish a new immutable candidate/source snapshot; preserve previous installed source and binaries. Run actual packaged compat/read-only checks.
- [x] Stop only the owned agent, install/enable the candidate with a verified rollback path; confirm installed hash, owned task, capture_ready and initial/steady timing numbers.
- [x] Ask for one concentrated Windows-music/volume/mute comparison. State measured software timing separately from user-estimated end-to-end latency. Do not declare 30-minute playback or video synchronization passed.

## Review focus

Latest target during pending readback; hardware zero versus our own mute; very low volume below receiver floor; startup first-sound retention and silence removal; capture epoch and driver format changes. Tests cover the responsible branch rather than source-text assertions.

Software evidence: .NET 387 passed; Rust 71 passed / 2 original ignored; Clippy passed. Static CRT release and import audit passed with 144 unconfirmed imports retained. Fresh combined review found two Important ordering/clear-boundary cases; both watched RED then fixed in one pass. Hardware remains pending.

Hardware startup finding: a8aed69 reached capture_ready but failed before first media packet because an asynchronous initial volume read required another poll and the fixed 1s pending interval exhausted the existing 2s preroll budget. Baseline restored. A five-second zero-signal device probe confirmed the delayed read; pending-only polling is now 100ms with a real localhost Worker/Connection RED→GREEN regression (131ms/118ms confirmations). Full Rust72 pass/2 original ignored, Clippy clean. Replacement candidate requires a forced initial-volume-change trial before playback acceptance.

Actual replacement bbccbfc passed the forced device14→Windows-mapped39 initialization and emitted media/capture metrics beyond the former failure window. Capture→send first p95=25.35ms, later samples21–29ms. User reports a major improvement and approximately1second remaining total delay. This follow-up has actual user feedback; precise acoustic latency/video sync/full30minute stability and the parent plan's other hardware gates remain open.
