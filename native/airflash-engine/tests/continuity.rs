//! Short deterministic/local-network fault injection. Never connects to a speaker.
use airflash_engine::{
    crypto::Cipher,
    rtp::{PCM_BYTES, Packetizer, RATE, Retransmit},
    rtsp::{Cancellation, Connection},
    transport::*,
};
use std::{
    io::Write,
    net::{TcpListener, TcpStream, UdpSocket},
    sync::{
        Arc, Mutex,
        atomic::{AtomicUsize, Ordering},
    },
    thread,
    time::{Duration, Instant},
};
fn pair() -> (Connection, TcpStream) {
    let l = TcpListener::bind("127.0.0.1:0").unwrap();
    let c = Connection::connect(l.local_addr().unwrap(), Cancellation::default()).unwrap();
    let (p, _) = l.accept().unwrap();
    (c, p)
}
fn until(mut f: impl FnMut() -> bool) {
    let end = Instant::now() + Duration::from_secs(3);
    while !f() {
        assert!(Instant::now() < end, "condition timed out");
        thread::sleep(Duration::from_millis(2));
    }
}
fn media() -> (MediaTransport, UdpSocket, UdpSocket) {
    let audio = UdpSocket::bind("127.0.0.1:0").unwrap();
    let control = UdpSocket::bind("127.0.0.1:0").unwrap();
    let audio_peer = UdpSocket::bind("127.0.0.1:0").unwrap();
    let control_peer = UdpSocket::bind("127.0.0.1:0").unwrap();
    audio.connect(audio_peer.local_addr().unwrap()).unwrap();
    let transport = MediaTransport::new(
        Packetizer::new([7; 32], 65534, u32::MAX - 600, 1),
        audio,
        control,
        control_peer.local_addr().unwrap(),
    )
    .unwrap();
    (transport, audio_peer, control_peer)
}
fn fast() -> FeedbackTiming {
    FeedbackTiming {
        interval: Duration::from_millis(10),
        soft: Duration::from_millis(35),
        hard: Duration::from_millis(300),
    }
}
#[test]
fn feedback_grace_keeps_one_request_and_accepts_late_encrypted_reply() {
    let (mut conn, peer) = pair();
    conn.encrypt([1; 32], [2; 32]);
    let health = Health::default();
    let server = thread::spawn(move || {
        let mut c = Connection::from_stream(peer, Cancellation::default()).unwrap();
        c.encrypt([2; 32], [1; 32]);
        let msg = c.read().unwrap();
        assert!(msg.first.starts_with("POST /feedback"));
        let seq = &msg.headers["cseq"];
        thread::sleep(Duration::from_millis(90));
        // A second request must not be sent while the original is still pending.
        assert!(
            c.read_for(Duration::from_millis(10), None)
                .unwrap()
                .is_none()
        );
        c.write(format!("RTSP/1.0 200 OK\r\nCSeq: {seq}\r\nContent-Length: 0\r\n\r\n").as_bytes())
            .unwrap();
        thread::sleep(Duration::from_millis(50));
    });
    let worker = FeedbackWorker::start(
        Arc::new(Mutex::new(conn)),
        "test".into(),
        health.clone(),
        fast(),
    )
    .unwrap();
    until(|| health.snapshot().feedback_delayed);
    until(|| health.snapshot().feedback_successes == 1);
    health.check().unwrap();
    assert!(!health.snapshot().feedback_delayed);
    let notices = health.notices();
    assert!(notices.iter().any(|n| n["code"] == "feedback_delayed"));
    assert!(notices.iter().any(|n| n["code"] == "feedback_recovered"));
    drop(worker);
    server.join().unwrap();
}
#[test]
fn feedback_retries_only_complete_transient_responses() {
    for statuses in [
        vec![503, 503, 200],
        vec![503, 503, 503],
        vec![403],
        vec![454],
    ] {
        let (conn, peer) = pair();
        let expected = statuses.clone();
        let health = Health::default();
        let server = thread::spawn(move || {
            let mut c = Connection::from_stream(peer, Cancellation::default()).unwrap();
            for status in statuses {
                let msg = c.read().unwrap();
                let seq = &msg.headers["cseq"];
                c.write(
                    format!("RTSP/1.0 {status} Result\r\nCSeq: {seq}\r\nContent-Length: 0\r\n\r\n")
                        .as_bytes(),
                )
                .unwrap();
            }
            thread::sleep(Duration::from_millis(100));
        });
        let worker = FeedbackWorker::start(
            Arc::new(Mutex::new(conn)),
            "test".into(),
            health.clone(),
            fast(),
        )
        .unwrap();
        until(|| health.snapshot().feedback_successes > 0 || health.check().is_err());
        if expected.last() == Some(&200) {
            health.check().unwrap();
            assert_eq!(health.snapshot().feedback_failures, 2);
        } else {
            let error = health.check().unwrap_err();
            let fault = error.downcast_ref::<Fault>().unwrap();
            assert_eq!(fault.retryable, expected[0] != 403);
            assert_eq!(fault.channel, "feedback");
        }
        drop(worker);
        server.join().unwrap();
    }
}
#[test]
fn feedback_deadline_cseq_error_and_stop_are_bounded() {
    for wrong_seq in [false, true] {
        let (conn, peer) = pair();
        let health = Health::default();
        let server = thread::spawn(move || {
            let mut c = Connection::from_stream(peer, Cancellation::default()).unwrap();
            c.read().unwrap();
            if wrong_seq {
                c.write(b"RTSP/1.0 200 OK\r\nCSeq: 99\r\nContent-Length: 0\r\n\r\n")
                    .unwrap();
            }
            thread::sleep(Duration::from_millis(150));
        });
        let worker = FeedbackWorker::start(
            Arc::new(Mutex::new(conn)),
            "test".into(),
            health.clone(),
            FeedbackTiming {
                hard: Duration::from_millis(100),
                ..fast()
            },
        )
        .unwrap();
        until(|| health.check().is_err());
        let error = health.check().unwrap_err();
        let fault = error.downcast_ref::<Fault>().unwrap();
        assert_eq!(
            fault.code,
            if wrong_seq {
                "protocol_error"
            } else {
                "feedback_timeout"
            }
        );
        drop(worker);
        server.join().unwrap();
    }
    let (conn, _peer) = pair();
    let worker = FeedbackWorker::start(
        Arc::new(Mutex::new(conn)),
        "test".into(),
        Health::default(),
        fast(),
    )
    .unwrap();
    thread::sleep(Duration::from_millis(25));
    let start = Instant::now();
    drop(worker);
    assert!(start.elapsed() < Duration::from_millis(250));
}
#[test]
fn two_members_keep_media_while_events_idle_and_one_feedback_is_delayed() {
    let mut workers = Vec::new();
    let mut servers = Vec::new();
    let mut senders = Vec::new();
    let mut event_peers = Vec::new();
    let stop = Cancellation::default();
    let requests = Arc::new(AtomicUsize::new(0));
    for index in 0..2 {
        let health = Health::default();
        let (mut conn, event_peer) = pair();
        conn.set_timeout(Duration::from_millis(10)).unwrap();
        conn.encrypt([1; 32], [2; 32]);
        workers.push(
            EventWorker::start(
                conn,
                index.to_string(),
                health.clone(),
                Cancellation::default(),
            )
            .unwrap(),
        );
        event_peers.push(event_peer);
        let (conn, peer) = pair();
        let done = stop.clone();
        let counter = requests.clone();
        servers.push(thread::spawn(move || {
            let mut c = Connection::from_stream(peer, done.clone()).unwrap();
            while !done.is_cancelled() {
                let msg = match c.read_for(Duration::from_millis(10), Some(&done)) {
                    Ok(Some(m)) => m,
                    Ok(None) => continue,
                    Err(_) => break,
                };
                counter.fetch_add(1, Ordering::Relaxed);
                if index == 0 {
                    thread::sleep(Duration::from_millis(65));
                }
                let seq = &msg.headers["cseq"];
                if c.write(
                    format!("RTSP/1.0 200 OK\r\nCSeq: {seq}\r\nContent-Length: 0\r\n\r\n")
                        .as_bytes(),
                )
                .is_err()
                {
                    break;
                }
            }
        }));
        let feedback = FeedbackWorker::start(
            Arc::new(Mutex::new(conn)),
            index.to_string(),
            health.clone(),
            fast(),
        )
        .unwrap();
        let (m, a, c) = media();
        senders.push((m, a, c, health, feedback));
    }
    let start = Instant::now();
    let mut schedule = MediaSchedule::new(start, RATE);
    let pcm = vec![0; PCM_BYTES];
    while start.elapsed() < Duration::from_millis(250) {
        let now = Instant::now();
        if now < schedule.deadline() {
            thread::sleep(Duration::from_millis(1));
            continue;
        }
        let skip = schedule.recover(now, true).unwrap();
        for (m, _, _, h, _) in &mut senders {
            m.packetizer.skip_packets(skip);
            m.send(&pcm, false, now, schedule.deadline(), h).unwrap();
            m.service_retransmits();
        }
        schedule.sent();
    }
    // Event channels outlive many ordinary read deadlines, then still decrypt.
    for peer in &mut event_peers {
        let bytes = Cipher::new([2; 32])
            .records(b"POST /event HTTP/1.1\r\nCSeq: 1\r\nContent-Length: 0\r\n\r\n")
            .unwrap();
        peer.write_all(&bytes[..1]).unwrap();
        thread::sleep(Duration::from_millis(5));
        peer.write_all(&bytes[1..]).unwrap();
    }
    until(|| {
        senders
            .iter()
            .all(|(_, _, _, h, _)| h.snapshot().event_messages == 1)
    });
    for (m, a, _, h, _) in &senders {
        h.check().unwrap();
        assert!(m.metrics.packets_sent > 10);
        assert!(h.snapshot().feedback_successes > 0);
        a.set_nonblocking(true).unwrap();
        let mut buf = [0; 2048];
        let mut received = 0;
        while a.recv(&mut buf).is_ok() {
            received += 1;
        }
        assert_eq!(received, m.metrics.packets_sent);
    }
    assert!(requests.load(Ordering::Relaxed) > 4);
    drop(workers);
    drop(senders);
    stop.cancel();
    for server in servers {
        server.join().unwrap();
    }
}
#[test]
fn scheduler_stalls_advance_both_members_without_reusing_nonces() {
    for ms in [80, 200, 500] {
        let start = Instant::now();
        let mut schedule = MediaSchedule::new(start, RATE);
        let mut left = Packetizer::new([1; 32], 65534, u32::MAX - 100, 1);
        let mut right = Packetizer::new([2; 32], 10, u32::MAX - 100, 2);
        let pcm = vec![0; PCM_BYTES];
        let original = left.packet(&pcm, false).unwrap();
        right.packet(&pcm, false).unwrap();
        schedule.sent();
        let now = start + Duration::from_millis(ms);
        let skipped = schedule.recover(now, true).unwrap();
        assert!(skipped > 0);
        left.skip_packets(skipped);
        right.skip_packets(skipped);
        assert_eq!(left.timestamp, right.timestamp);
        assert_eq!(right.seq.wrapping_sub(left.seq), 12);
        let next = left
            .packet_at(&pcm, false, now, now + Duration::from_millis(150))
            .unwrap();
        assert_eq!(
            u64::from_le_bytes(original[original.len() - 8..].try_into().unwrap()),
            0
        );
        assert_eq!(
            u64::from_le_bytes(next[next.len() - 8..].try_into().unwrap()),
            1
        );
        assert!(now.duration_since(schedule.deadline()) < Duration::from_millis(8));
        assert_eq!(schedule.recoveries, 1);
        assert!(MediaSchedule::new(start, RATE).recover(now, false).is_err());
    }
}
#[test]
fn temporary_udp_failure_preserves_session_and_persistent_failure_is_typed() {
    let (mut m, _, _) = media();
    let h = Health::default();
    let start = Instant::now();
    m.reset_watchdog(start);
    m.observe_send(
        Err(std::io::ErrorKind::WouldBlock.into()),
        start + Duration::from_secs(1),
        &h,
    )
    .unwrap();
    m.observe_send(Ok(()), start + Duration::from_secs(2), &h)
        .unwrap();
    assert_eq!(m.metrics.media_send_errors, 1);
    assert_eq!(m.metrics.packets_sent, 1);
    assert_eq!(h.notices().len(), 2);
    let error = m
        .observe_send(
            Err(std::io::ErrorKind::NetworkUnreachable.into()),
            start + Duration::from_secs(14),
            &h,
        )
        .unwrap_err();
    assert_eq!(
        error.downcast_ref::<Fault>().unwrap().code,
        "media_send_timeout"
    );
}
#[test]
fn retransmit_deadlines_retention_missing_and_ciphertext() {
    let now = Instant::now();
    let mut p = Packetizer::new([9; 32], 65535, 0, 1);
    p.configure_latency(Duration::from_millis(150));
    let packet = p
        .packet_at(
            &vec![0; PCM_BYTES],
            false,
            now,
            now + Duration::from_millis(150),
        )
        .unwrap();
    let Retransmit::Packet(reply) = p.retransmit_packet(65535, now + Duration::from_millis(149))
    else {
        panic!("missing")
    };
    assert_eq!(&reply[4..], packet);
    assert!(matches!(
        p.retransmit_packet(65535, now + Duration::from_millis(150)),
        Retransmit::Expired
    ));
    assert!(matches!(p.retransmit_packet(0, now), Retransmit::Missing));
    p.packet_at(
        &vec![0; PCM_BYTES],
        false,
        now + Duration::from_secs(2),
        now + Duration::from_secs(3),
    )
    .unwrap();
    assert!(matches!(
        p.retransmit_packet(65535, now + Duration::from_secs(2)),
        Retransmit::Missing
    ));
    p.configure_latency(Duration::from_secs(2));
    let packet = p
        .packet_at(
            &vec![0; PCM_BYTES],
            false,
            now + Duration::from_secs(2),
            now + Duration::from_secs(4),
        )
        .unwrap();
    assert!(matches!(
        p.retransmit_packet(
            u16::from_be_bytes([packet[2], packet[3]]),
            now + Duration::from_millis(3500)
        ),
        Retransmit::Packet(_)
    ));
}
#[test]
fn retransmit_flood_is_bounded_and_does_not_prevent_new_audio() {
    let (mut m, a, c) = media();
    m.configure_latency(Duration::from_secs(2), false);
    let health = Health::default();
    let pcm = vec![0; PCM_BYTES];
    m.send(&pcm, false, Instant::now(), Instant::now(), &health)
        .unwrap();
    let address = std::net::SocketAddr::from(([127, 0, 0, 1], m.control_port().unwrap()));
    // Requests cross sequence wrap and deliberately include many missing packets.
    for _ in 0..64 {
        c.send_to(&[0x80, 0xd5, 0, 1, 255, 254, 255, 255], address)
            .unwrap();
    }
    for _ in 0..8 {
        let before = m.metrics.retransmits_sent;
        let requests = m.metrics.retransmit_requests;
        m.send(&pcm, false, Instant::now(), Instant::now(), &health)
            .unwrap();
        m.service_retransmits();
        assert!(m.metrics.retransmits_sent - before <= 32);
        assert!(m.metrics.retransmit_requests - requests <= 16);
        assert!(m.metrics()["retransmit_pending"].as_u64().unwrap() <= 512);
    }
    assert_eq!(m.metrics.packets_sent, 9);
    assert!(m.metrics.retransmit_queue_drops > 0);
    a.set_nonblocking(true).unwrap();
    let mut b = [0; 2048];
    let mut count = 0;
    while a.recv(&mut b).is_ok() {
        count += 1;
    }
    assert_eq!(count, 9);
    assert!(Packetizer::retransmit_request(&[0; 8]).is_none());
}

#[test]
fn events_report_real_eof_and_stop_without_waiting_for_idle_timeout() {
    let (conn, peer) = pair(); let health = Health::default();
    let worker = EventWorker::start(conn, "test".into(), health.clone(), Cancellation::default()).unwrap();
    drop(peer); until(|| health.check().is_err());
    let error = health.check().unwrap_err(); let fault = error.downcast_ref::<Fault>().unwrap();
    assert_eq!(fault.code,"peer_closed"); assert_eq!(fault.channel,"events"); assert!(fault.retryable);
    drop(worker);
    let (conn, _peer) = pair();
    let worker = EventWorker::start(conn, "test".into(), Health::default(), Cancellation::default()).unwrap();
    let now=Instant::now(); drop(worker); assert!(now.elapsed()<Duration::from_millis(250));
}
#[test]
fn requested_packets_arrive_as_original_ciphertext_out_of_order_and_across_wrap() {
    let (mut m,a,c)=media(); m.configure_latency(Duration::from_secs(2),false);
    a.set_read_timeout(Some(Duration::from_secs(1))).unwrap(); c.set_read_timeout(Some(Duration::from_secs(1))).unwrap();
    let mut originals=std::collections::BTreeMap::new(); let h=Health::default(); let mut buffer=[0;2048];
    for value in [1,2,3] {
        m.send(&vec![value;PCM_BYTES],false,Instant::now(),Instant::now(),&h).unwrap();
        let n=a.recv(&mut buffer).unwrap(); originals.insert(u16::from_be_bytes([buffer[2],buffer[3]]),buffer[..n].to_vec());
    }
    let next_seq=m.packetizer.seq;
    let address=std::net::SocketAddr::from(([127,0,0,1],m.control_port().unwrap()));
    for seq in [0u16,65535,65534,0] {
        let [hi,lo]=seq.to_be_bytes(); c.send_to(&[0x80,0xd5,0,1,hi,lo,0,1],address).unwrap();
        let before=m.metrics.retransmits_sent;
        until(||{m.service_retransmits();m.metrics.retransmits_sent>before});
        let n=c.recv(&mut buffer).unwrap(); assert_eq!(&buffer[..2],&[0x80,0xd6]);
        assert_eq!(&buffer[4..n],originals.get(&seq).unwrap());
    }
    assert_eq!(m.packetizer.seq,next_seq); assert_eq!(m.metrics.retransmits_sent,4);
}

fn request_cached(media: &mut MediaTransport, peer: &UdpSocket, sequence: u16) {
    let processed = |m: &MediaMetrics| m.retransmits_sent + m.retransmit_expired + m.retransmit_missing + m.retransmit_send_errors;
    let before = processed(&media.metrics);
    let [hi, lo] = sequence.to_be_bytes();
    peer.send_to(&[0x80, 0xd5, 0, 1, hi, lo, 0, 1], std::net::SocketAddr::from(([127, 0, 0, 1], media.control_port().unwrap()))).unwrap();
    until(|| { media.service_retransmits(); processed(&media.metrics) > before });
}

#[test]
fn media_transport_unknown_latency_is_null_and_configured_values_remain_explicit() {
    let (mut media, _, _) = media();
    assert!(media.metrics()["receiver_latency_ms"].is_null());
    assert_eq!(media.metrics()["receiver_latency_estimated"], true);
    for estimated in [true, false] {
        media.configure_latency(Duration::from_millis(100), estimated);
        assert_eq!(media.metrics()["receiver_latency_ms"], 100.0);
        assert_eq!(media.metrics()["receiver_latency_estimated"], estimated);
    }
    media.configure_latency(Duration::ZERO, true);
    assert_eq!(media.metrics()["receiver_latency_ms"], 0.0);
}

#[test]
fn media_transport_unknown_latency_recovers_cached_ciphertext_by_sent_age() {
    let (mut media, audio, control) = media();
    audio.set_read_timeout(Some(Duration::from_millis(100))).unwrap();
    control.set_read_timeout(Some(Duration::from_millis(100))).unwrap();
    let sent = Instant::now();
    media.send(&vec![0; PCM_BYTES], true, sent, sent - Duration::from_secs(2), &Health::default()).unwrap();
    let mut bytes = [0; 2048];
    let length = audio.recv(&mut bytes).unwrap();
    assert!(sent.elapsed() < Duration::from_millis(100), "unknown latency must not delay initial media");
    let original = bytes[..length].to_vec();
    let sequence = u16::from_be_bytes([bytes[2], bytes[3]]);
    for age in [300, 500] {
        thread::sleep(Duration::from_millis(age).saturating_sub(sent.elapsed()));
        request_cached(&mut media, &control, sequence);
        let length = control.recv(&mut bytes).unwrap();
        assert_eq!(&bytes[..2], &[0x80, 0xd6]);
        assert_eq!(&bytes[4..length], original);
    }
    assert_eq!(media.metrics.retransmits_sent, 2);
    thread::sleep(Duration::from_millis(1100).saturating_sub(sent.elapsed()));
    request_cached(&mut media, &control, sequence);
    assert!(control.recv(&mut bytes).is_err());
    assert_eq!(media.metrics.retransmit_expired, 1);
}

#[test]
fn media_transport_auto_minimum_does_not_expire_recovery_at_the_minimum() {
    let (mut media, audio, control) = media();
    media.configure_receiver_window(Some(Duration::from_millis(100)), None);
    audio.set_read_timeout(Some(Duration::from_millis(100))).unwrap();
    control.set_read_timeout(Some(Duration::from_millis(100))).unwrap();
    let sent = Instant::now();
    media.send(&vec![1; PCM_BYTES], true, sent, sent - Duration::from_secs(2), &Health::default()).unwrap();
    let mut bytes = [0; 2048];
    let length = audio.recv(&mut bytes).unwrap();
    assert!(sent.elapsed() < Duration::from_millis(100), "auto minimum must not delay initial media");
    let original = bytes[..length].to_vec();
    let sequence = u16::from_be_bytes([bytes[2], bytes[3]]);
    assert_eq!(media.metrics()["receiver_latency_ms"], 100.0);
    assert_eq!(media.metrics()["receiver_latency_estimated"], false);
    for age in [300, 500] {
        thread::sleep(Duration::from_millis(age).saturating_sub(sent.elapsed()));
        request_cached(&mut media, &control, sequence);
        let length = control.recv(&mut bytes).unwrap();
        assert_eq!(&bytes[..2], &[0x80, 0xd6]);
        assert_eq!(&bytes[4..length], original);
    }
    assert_eq!(media.metrics.retransmits_sent, 2);
    assert_eq!(media.metrics.nonzero_pcm_packets_sent, 1);
    thread::sleep(Duration::from_millis(1100).saturating_sub(sent.elapsed()));
    request_cached(&mut media, &control, sequence);
    assert!(control.recv(&mut bytes).is_err());
    assert_eq!(media.metrics.retransmit_expired, 1);
}

#[test]
fn media_transport_auto_maximum_sets_recovery_without_inventing_measured_latency() {
    for minimum in [Some(Duration::from_millis(50)), None] {
        let (mut media, audio, _) = media();
        media.configure_receiver_window(minimum, Some(Duration::from_millis(100)));
        assert_eq!(media.metrics()["receiver_latency_ms"].as_f64(), minimum.map(|_| 50.0));
        assert_eq!(media.metrics()["receiver_latency_estimated"], minimum.is_none());
        let sent = Instant::now();
        media.send(&vec![1; PCM_BYTES], true, sent, sent, &Health::default()).unwrap();
        audio.set_read_timeout(Some(Duration::from_millis(100))).unwrap();
        let mut bytes = [0; 2048];
        let length = audio.recv(&mut bytes).unwrap();
        let sequence = u16::from_be_bytes([bytes[2], bytes[3]]);
        let Retransmit::Packet(reply) = media.packetizer.retransmit_packet(sequence, sent + Duration::from_millis(75)) else {
            panic!("receiver maximum must allow recovery beyond its minimum");
        };
        assert_eq!(&reply[4..], &bytes[..length]);
        for age in [100, 150] {
            assert!(matches!(media.packetizer.retransmit_packet(sequence, sent + Duration::from_millis(age)), Retransmit::Expired));
        }
        let delayed = sent + Duration::from_millis(200);
        let sequence = media.packetizer.seq;
        media.send(&vec![0; PCM_BYTES], false, delayed, sent, &Health::default()).unwrap();
        assert!(matches!(media.packetizer.retransmit_packet(sequence, delayed), Retransmit::Expired));
    }
}

#[test]
fn media_transport_auto_retention_uses_maximum_and_resets_when_it_is_unknown() {
    let (mut media, audio, _) = media();
    media.configure_receiver_window(Some(Duration::from_millis(50)), Some(Duration::from_secs(2)));
    let sent = Instant::now();
    media.send(&vec![0; PCM_BYTES], true, sent, sent + Duration::from_secs(2), &Health::default()).unwrap();
    audio.set_read_timeout(Some(Duration::from_millis(100))).unwrap();
    let mut bytes = [0; 2048];
    audio.recv(&mut bytes).unwrap();
    let sequence = u16::from_be_bytes([bytes[2], bytes[3]]);
    assert!(matches!(media.packetizer.retransmit_packet(sequence, sent + Duration::from_millis(2249)), Retransmit::Packet(_)));
    assert!(matches!(media.packetizer.retransmit_packet(sequence, sent + Duration::from_millis(2250)), Retransmit::Expired));
    media.configure_receiver_window(None, None);
    assert!(media.metrics()["receiver_latency_ms"].is_null());
    assert_eq!(media.metrics()["receiver_latency_estimated"], true);
    assert!(matches!(media.packetizer.retransmit_packet(sequence, sent + Duration::from_millis(999)), Retransmit::Packet(_)));
    assert!(matches!(media.packetizer.retransmit_packet(sequence, sent + Duration::from_secs(1)), Retransmit::Expired));
}

#[test]
fn media_transport_explicit_latency_overrides_auto_window_including_zero() {
    for latency in [Duration::ZERO, Duration::from_millis(100)] {
        let (mut media, _, _) = media();
        media.configure_receiver_window(Some(Duration::from_millis(50)), Some(Duration::from_secs(2)));
        media.configure_latency(latency, false);
        let sent = Instant::now();
        let sequence = media.packetizer.seq;
        media.send(&vec![0; PCM_BYTES], true, sent, sent, &Health::default()).unwrap();
        assert_eq!(media.metrics()["receiver_latency_ms"], latency.as_secs_f64() * 1000.0);
        assert_eq!(media.metrics()["receiver_latency_estimated"], false);
        assert!(matches!(media.packetizer.retransmit_packet(sequence, sent + latency), Retransmit::Expired));
    }
}

#[test]
fn media_transport_explicit_100ms_deadline_is_not_extended() {
    for estimated in [true, false] {
        let (mut media, audio, control) = media();
        media.configure_latency(Duration::from_millis(100), estimated);
        audio.set_read_timeout(Some(Duration::from_millis(100))).unwrap();
        control.set_read_timeout(Some(Duration::from_millis(100))).unwrap();
        let sent = Instant::now();
        media.send(&vec![0; PCM_BYTES], true, sent, sent, &Health::default()).unwrap();
        let mut bytes = [0; 2048];
        audio.recv(&mut bytes).unwrap();
        thread::sleep(Duration::from_millis(300).saturating_sub(sent.elapsed()));
        request_cached(&mut media, &control, u16::from_be_bytes([bytes[2], bytes[3]]));
        assert!(control.recv(&mut bytes).is_err());
        assert_eq!(media.metrics.retransmit_expired, 1);
        assert_eq!(media.metrics.retransmits_sent, 0);
    }
}

#[test]
fn media_transport_counts_only_successful_nonzero_pcm_sends() {
    let (mut media, audio, _) = media();
    audio.set_read_timeout(Some(Duration::from_millis(100))).unwrap();
    let mut pcm = vec![0; PCM_BYTES];
    pcm[3] = 1;
    media.send(&pcm, true, Instant::now(), Instant::now(), &Health::default()).unwrap();
    audio.recv(&mut [0; 2048]).unwrap();
    assert_eq!(media.metrics()["nonzero_pcm_packets_sent"], 1);
    pcm.fill(0);
    media.send(&pcm, false, Instant::now(), Instant::now(), &Health::default()).unwrap();
    audio.recv(&mut [0; 2048]).unwrap();
    assert_eq!(media.metrics.packets_sent, 2);
    assert_eq!(media.metrics()["nonzero_pcm_packets_sent"], 1);
    let mut failed = MediaTransport::new(Packetizer::new([8; 32], 1, 0, 1),
        UdpSocket::bind("127.0.0.1:0").unwrap(), UdpSocket::bind("127.0.0.1:0").unwrap(), audio.local_addr().unwrap()).unwrap();
    pcm[0] = 1;
    failed.send(&pcm, true, Instant::now(), Instant::now(), &Health::default()).unwrap();
    assert_eq!(failed.metrics.media_send_errors, 1);
    assert_eq!(failed.metrics.packets_sent, 0);
    assert_eq!(failed.metrics()["nonzero_pcm_packets_sent"], 0);
}
