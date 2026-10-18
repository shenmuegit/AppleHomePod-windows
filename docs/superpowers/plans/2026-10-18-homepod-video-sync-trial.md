# HomePod video synchronization trial

**Goal:** Reduce the remaining approximately one-second user-observed delay and verify flash/tone alignment, keeping Windows native daily controls and deferring physical disconnect.

**Scope:** A bounded follow-up to the approved native-output design and latency work. Preserve the accepted bbccbfc EXE/source and current calibration. No driver, security, network, RTP/NTP anchor, codec or volume-mapping changes. The temporary AV test page is a diagnostic artifact, not a new product panel.

## Evidence and trial

- Baseline is native 500ms requested delay; current capture-to-send samples are21–29ms. The requested value is not measured acoustic latency.
- A five-second page schedules two 80ms sine bursts (peak≤0.05) and visual pulses at1s/3s, using one browser audio clock. No autoplay or microphone. User starts it when present; keep it visible. Visual/browser scheduling adds uncertainty, so record estimates as subjective.
- Compare the same page and volume under500→200→500 if needed. Only Native target changes, including the corresponding SETUP range and estimated retransmission deadline. Capture target/volume/clock formula remain unchanged.
- 200ms is experimental. Community source uses wider250..1000ms ranges; narrower requests may reduce recovery margin. Inspect media/retransmission metrics and user-reported interruptions, not just successful packets.
- A receiver may ignore a request. If the comparison does not reduce actual AV skew, preserve/revert the working500ms package and investigate presentation-time/driver reporting. Do not compensate by adding per-player settings or a daily panel.

## Steps

- [ ] Establish the flash/tone baseline on the current installed candidate.
- [ ] Update the existing wire/snapshot test to200, observe RED, change the single Native target, and pass the full.NET suite. Log existing transport diagnostics at the existing15s sampling cadence.
- [ ] Publish an isolated200ms candidate, verify exact embedded engine/compatibility, and activate only after the baseline response. Require media/capture readiness beyond the former startup failure window, with rollback tobbccbfc.
- [ ] Repeat the same AV page, record perceived skew and transport counters, choose the stable improved target or revert, preserve exact source and candidate provenance. Leave precise acoustic measurement,30-minute qualification and disconnect unclaimed.
