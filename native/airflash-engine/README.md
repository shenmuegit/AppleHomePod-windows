# Native AirPlay engine

Windows-only Rust sidecar; JSONL v1 on stdin/stdout. Diagnostics must not contain keys or PINs.
Python UI commands use `start` (live WASAPI); finite tests use `probe` (<=5000ms, gain<=0.1,
440Hz WAV peak<=0.05). EOF cancels all owned work. `stop` and gain changes apply only to the
matching session. `pair` emits `pin_required`; `pair_pin` completes pairing, validates the
accessory signature and writes version-1, user-bound DPAPI credentials. Old pyatv credentials
are neither read nor changed.

Live `start` accepts optional `equalizer` settings (`enabled`, `preamp_db`, ten
`band_gains_db` values). Missing settings bypass EQ. `set_equalizer` accepts the
same settings plus a positive increasing `sequence` for the matching live session;
`equalizer_changed` reports automatic attenuation and effective preamp, and
`equalizer_error` rejects invalid changes without ending playback. Gains must be
finite and within -12..12 dB. Finite probes reject enabled EQ. Both stereo members
receive the same PCM after resampling, EQ, and master gain. Updates crossfade over
20 ms with no additional buffering.

Native-output mode prepares one explicit render endpoint with `arm_capture`
(`capture_id`: UUID, `endpoint_id`, `sample_rate`: 44100). `capture_ready` reports
epoch 1 and the actual opaque WASAPI session instance ID. Before network discovery,
`begin_capture {capture_id,epoch}` freezes the most recent 100 ms; repeated begin
is idempotent. `start` then adds `capture_id`, `capture_epoch`, `await_volume_gate:true`.
Partial keys, stale epochs, concurrent ownership, or a different source are rejected.
Legacy start/probe commands remain available when capture is disarmed.

While the volume gate is closed, PCM is retained (maximum 2000 ms / 88,200 frames),
and volume/control events continue. Overflow is a typed `capture_preroll_overflow`
failure; buffered sound is never silently trimmed during initialization.
`set_capture_gate {capture_id,epoch,open,clear_pending}` releases or clears the
queue. Resolved mute clears old PCM and opens the gate with gain zero so silence
continues to drain. The scheduler rebases before its first send. `streaming` means
control is ready; actual send timestamps are emitted separately as `first_send`.
Startup backlog keeps its delay at the normal playback rate and is not immediately
shrunk to the legacy 60 ms capacity; clock drift/long playback need real qualification.

After acknowledged transport stop, `reset_capture {capture_id,epoch}` clears the
queue and increments the epoch with overflow checking; stale resampler batches are
excluded by their capture timestamps. `disarm_capture {capture_id}` releases the
source after transport stop. EOF releases both transport and capture. No PCM crosses
JSONL. Capture uses its own WASAPI session GUID and supports no recording fallback.

Build with `scripts/build-native.ps1 -Check`. The ignored `soak` test runs two localhost UDP
receivers for 30 wall-clock minutes; it never connects to a speaker. Other tests cover
independent SRP vectors, simulated HAP peers, invalid identities/signatures, HAP records,
RTSP fragmentation/bounds/cancellation, sequence wrap and independent ALAC decoding.

Protocol references (wire fields/behavior, not linked protocol implementations):
- HAP and AirPlay observations in postlund/pyatv (MIT).
- AirPlay PTP profile documented by owntone/libairptp (MIT).
- Apple ALAC codec format via alac-encoder (MIT OR Apache-2.0).
- IEEE 1588 message layouts and RTP sequence/time semantics.

Foundation libraries: RustCrypto sha2/hkdf/ChaCha20-Poly1305, dalek Ed25519/X25519,
num-bigint (SRP arithmetic), Rubato (resampling), alac-encoder, windows-rs.
The code contains no dependency on a third-party complete AirPlay sender.

Limitations: first-device scope is a single native HomePod stereo pair, Windows x64, stereo PCM.
ALAC and persistent pairing have independent/simulated tests; hardware qualification covers
PCM transient authentication. No cross-model 200ms guarantee. The PTP implementation is
an AirPlay unicast master, not a general-purpose IEEE 1588 daemon or full BMCA implementation.
