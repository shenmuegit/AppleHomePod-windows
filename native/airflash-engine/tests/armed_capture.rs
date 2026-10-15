//! Deterministic queue/IPC qualification; no audio endpoint or speaker is opened.
#![cfg(windows)]
use airflash_engine::{live::{ArmCapture, CaptureQueue, Frame}, rtp::FRAMES, transport::MediaSchedule};
use std::{io::Write, process::{Command, Stdio}, time::{Duration, Instant}};

fn frame(marker: u64, tone: bool) -> Frame {
    Frame { left: if tone { 0.02 } else { 0.0 }, right: 0.0, qpc: marker }
}

#[test]
fn armed_preroll_preserves_short_first_sound_through_handshake() {
    let mut queue = CaptureQueue::armed(44_100);
    for i in 0..4410 { queue.push(frame(i + 1, true)); }
    queue.begin(1).unwrap();
    queue.begin(1).unwrap(); // Repeated demand must not move the frozen start.
    for i in 4410..39690 { queue.push(frame(i + 1, false)); }
    assert!(queue.pop().is_err());
    assert_eq!(queue.len(), 39690);
    queue.set_gate(1, true, false).unwrap();
    assert_eq!(queue.pop().unwrap().unwrap().qpc, 1);
    let mut tones = 1;
    while let Some(f) = queue.pop().unwrap() { tones += usize::from(f.left != 0.0); }
    assert_eq!(tones, 4410);
}

#[test]
fn idle_is_rolling_but_buffering_overflow_is_explicit_and_latched() {
    let mut queue = CaptureQueue::armed(44_100);
    for i in 0..8820 { queue.push(frame(i, false)); }
    assert_eq!(queue.len(), 4410);
    queue.begin(1).unwrap();
    for i in 4410..88200 { queue.push(frame(i, true)); }
    assert_eq!(queue.len(), 88200);
    queue.push(frame(90000, true));
    let error = queue.check().unwrap_err();
    assert_eq!(error.downcast_ref::<airflash_engine::transport::Fault>().unwrap().code, "capture_preroll_overflow");
    assert!(queue.set_gate(1, true, false).is_err());
    assert_eq!(queue.len(), 88200);
    queue.reset(1, 100000).unwrap();
    assert_eq!(queue.epoch(), 2);
    assert_eq!(queue.len(), 0);
    queue.push(frame(99999, true)); // A resampler batch from the previous epoch.
    assert_eq!(queue.len(), 0);
    queue.push(frame(100001, true));
    assert_eq!(queue.len(), 1);
    assert!(queue.begin(1).is_err());
    assert!(queue.reset(1, 0).is_err());
    queue.begin(2).unwrap();
}

#[test]
fn resolved_mute_drains_fifteen_seconds_and_never_replays_muted_preroll() {
    let mut queue = CaptureQueue::armed(44_100);
    queue.push(frame(1, true));
    queue.begin(1).unwrap();
    queue.set_gate(1, true, true).unwrap();
    assert_eq!(queue.len(), 0);
    for packet in 0..(44100 * 15 / FRAMES) {
        for i in 0..FRAMES { queue.push(frame((packet * FRAMES + i) as u64 + 2, false)); }
        for _ in 0..FRAMES { assert_eq!(queue.pop().unwrap().unwrap().left, 0.0); }
    }
    queue.check().unwrap();
    assert_eq!(queue.len(), 0);
}

#[test]
fn first_open_rebases_schedule_without_dropping_startup_time() {
    let armed_at = Instant::now();
    let mut schedule = MediaSchedule::new(armed_at, 44100);
    let gate_at = armed_at + Duration::from_millis(800);
    schedule.rebase_before_first_send(gate_at).unwrap();
    assert_eq!(schedule.recover(gate_at, true).unwrap(), 0);
    assert_eq!(schedule.skipped_packets, 0);
    schedule.sent();
    assert!(schedule.rebase_before_first_send(gate_at).is_err());
}

#[test]
fn legacy_queue_keeps_sixty_ms_capacity() {
    let mut queue = CaptureQueue::legacy(44100);
    for i in 0..2746 { queue.push(frame(i, true)); }
    assert_eq!(queue.len(), 2646);
    assert_eq!(queue.dropped_frames(), 100);
    assert_eq!(queue.pop().unwrap().unwrap().qpc, 100);
}

#[test]
fn arm_contract_rejects_invalid_keys_and_concurrent_owners() {
    let valid = ArmCapture { capture_id: "266c0a08-eec2-4564-944b-53ca1a54e97f".into(), endpoint_id: "explicit-device-id".into(), sample_rate: 44100 };
    valid.validate(false, false).unwrap();
    assert!(valid.validate(true, false).is_err());
    assert!(valid.validate(false, true).is_err());
    assert!(ArmCapture { endpoint_id: String::new(), ..valid.clone() }.validate(false, false).is_err());
    assert!(ArmCapture { capture_id: "bad-key".into(), ..valid.clone() }.validate(false, false).is_err());
    assert!(ArmCapture { sample_rate: 48000, ..valid }.validate(false, false).is_err());
}

#[test]
fn start_rejects_partial_capture_identity_and_probe_keeps_its_limits() {
    use airflash_engine::session::ProbeOptions;
    let start = serde_json::json!({"peers":[{"host":"127.0.0.1"}],"source":"loopback", "duration_ms":0,"latency_ms":2000,"gain":0.5});
    serde_json::from_value::<ProbeOptions>(start.clone()).unwrap().validate_start().unwrap();
    for (key, value) in [("capture_id", serde_json::json!("266c0a08-eec2-4564-944b-53ca1a54e97f")), ("capture_epoch", serde_json::json!(1)), ("await_volume_gate", serde_json::json!(true))] {
        let mut partial = start.clone(); partial[key] = value;
        assert!(serde_json::from_value::<ProbeOptions>(partial).unwrap().validate_start().is_err());
    }
    let mut prepared = start;
    prepared["capture_id"] = serde_json::json!("266c0a08-eec2-4564-944b-53ca1a54e97f");
    prepared["capture_epoch"] = serde_json::json!(1);
    prepared["await_volume_gate"] = serde_json::json!(true);
    serde_json::from_value::<ProbeOptions>(prepared.clone()).unwrap().validate_start().unwrap();
    prepared["capture_epoch"] = serde_json::json!(0);
    assert!(serde_json::from_value::<ProbeOptions>(prepared).unwrap().validate_start().is_err());
    for (duration, gain) in [(5001, 0.1), (5000, 0.11)] {
        let probe = serde_json::json!({"peers":[{"host":"127.0.0.1"}],"source":"loopback", "duration_ms":duration,"latency_ms":2000,"gain":gain});
        assert!(serde_json::from_value::<ProbeOptions>(probe).unwrap().validate().is_err());
    }
}

#[test]
fn invalid_capture_commands_and_eof_release_process_without_audio() {
    let mut child = Command::new(env!("CARGO_BIN_EXE_airflash-engine"))
        .stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::piped()).spawn().unwrap();
    let mut input = child.stdin.take().unwrap();
    for (i, command, params) in [
        (1, "hello", serde_json::json!({})),
        (2, "arm_capture", serde_json::json!({"capture_id":"bad-key","endpoint_id":"", "sample_rate":44100})),
        (3, "begin_capture", serde_json::json!({"capture_id":"missing", "epoch":1})),
        (4, "reset_capture", serde_json::json!({"capture_id":"missing", "epoch":0})),
        (5, "set_capture_gate", serde_json::json!({"capture_id":"missing", "epoch":1,"open":true,"clear_pending":false,"extra":0})),
        (6, "disarm_capture", serde_json::json!({"capture_id":"missing"})),
    ] {
        writeln!(input, "{}", serde_json::json!({"version":1,"id":i.to_string(),"session_id":"contract", "command":command,"params":params})).unwrap();
    }
    drop(input);
    let deadline = Instant::now() + Duration::from_secs(5);
    while child.try_wait().unwrap().is_none() {
        if Instant::now() >= deadline { let _ = child.kill(); panic!("engine did not release on EOF"); }
        std::thread::sleep(Duration::from_millis(10));
    }
    let output = child.wait_with_output().unwrap();
    assert!(output.status.success());
    let lines: Vec<serde_json::Value> = String::from_utf8(output.stdout).unwrap().lines().map(|l| serde_json::from_str(l).unwrap()).collect();
    assert_eq!(lines.len(), 6);
    assert!(lines[0]["commands"].as_array().unwrap().iter().any(|c| c == "arm_capture"));
    assert!(lines[1..].iter().all(|e| e["event"] == "error" && e["code"] == "command_error"));
}
