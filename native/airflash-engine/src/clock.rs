//! Monotonic media clock and a bounded unicast PTPv2 probe master.
//! Implements the AirPlay PTP wire profile; no system clock is changed.
use crate::rtsp::Cancellation;
use anyhow::{Context, Result};
use std::{
    collections::BTreeMap,
    net::{IpAddr, UdpSocket},
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
#[derive(Clone)]
pub struct Clock {
    origin: Instant,
    epoch_ns: u64,
}
impl Default for Clock {
    fn default() -> Self {
        Self::new()
    }
}
impl Clock {
    pub fn new() -> Self {
        Self {
            origin: Instant::now(),
            epoch_ns: SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos() as u64,
        }
    }
    pub fn now_ns(&self) -> u64 {
        self.epoch_ns + self.origin.elapsed().as_nanos() as u64
    }
}
fn header(kind: u8, len: usize, clock: u64, seq: u16, flags: u16, interval: i8) -> Vec<u8> {
    let mut p = vec![0; len];
    p[0] = 0x10 | kind;
    p[1] = 2;
    p[2..4].copy_from_slice(&(len as u16).to_be_bytes());
    p[6..8].copy_from_slice(&flags.to_be_bytes());
    p[20..28].copy_from_slice(&clock.to_be_bytes());
    p[28..30].copy_from_slice(&0x8005u16.to_be_bytes());
    p[30..32].copy_from_slice(&seq.to_be_bytes());
    p[33] = interval as u8;
    p
}
fn timestamp(out: &mut [u8], ns: u64) {
    out[..6].copy_from_slice(&(ns / 1_000_000_000).to_be_bytes()[2..]);
    out[6..10].copy_from_slice(&((ns % 1_000_000_000) as u32).to_be_bytes());
}
#[derive(Default)]
struct ReceiverClock {
    id: u64,
    source: Option<[u8; 10]>,
    pending: Option<(u16, u64, i128)>,
    offset: Option<i128>,
    last_sync_ns: u64,
}
impl ReceiverClock {
    fn observe(&mut self, packet: &[u8], received_ns: u64) {
        if packet.len() < 34 || packet[0] >> 4 != 1 || packet[1] & 0xf != 2 { return; }
        let length = u16::from_be_bytes([packet[2], packet[3]]) as usize;
        if length > packet.len() || length < 34 { return; }
        let source: [u8; 10] = packet[20..30].try_into().unwrap();
        let sequence = u16::from_be_bytes([packet[30], packet[31]]);
        let correction = i128::from(i64::from_be_bytes(packet[8..16].try_into().unwrap())) / 65536;
        match packet[0] & 0xf {
            11 if length >= 64 => {
                let id = u64::from_be_bytes(packet[53..61].try_into().unwrap());
                if id == 0 { return; }
                if self.id != id || self.source != Some(source) { *self = Self::default(); }
                self.id = id; self.source = Some(source);
            }
            0 if length >= 44 && self.source == Some(source) && packet[6] & 2 != 0 => {
                self.pending = Some((sequence, received_ns, correction));
            }
            8 if length >= 44 && self.source == Some(source) => {
                let Some((expected, local_ns, sync_correction)) = self.pending else { return; };
                if sequence != expected || received_ns.saturating_sub(local_ns) > 250_000_000 { return; }
                let mut seconds = [0u8; 8]; seconds[2..].copy_from_slice(&packet[34..40]);
                let nanos = u32::from_be_bytes(packet[40..44].try_into().unwrap());
                if nanos >= 1_000_000_000 { return; }
                let Some(remote_ns) = u64::from_be_bytes(seconds).checked_mul(1_000_000_000)
                    .and_then(|s| s.checked_add(u64::from(nanos))) else { return; };
                let offset = i128::from(remote_ns) + sync_correction + correction - i128::from(local_ns);
                self.offset = Some(match self.offset {
                    Some(previous) if (offset - previous).abs() <= 1_000_000 => (7 * previous + offset) / 8,
                    _ => offset,
                });
                self.last_sync_ns = local_ns; self.pending = None;
            }
            _ => {}
        }
    }
    fn sample(&self, now_ns: u64) -> Option<(u64, u64)> {
        if now_ns.saturating_sub(self.last_sync_ns) > 3_000_000_000 { return None; }
        u64::try_from(i128::from(now_ns) + self.offset?).ok().map(|ns| (self.id, ns))
    }
}
fn tlv(data: &[u8]) -> Vec<u8> {
    let mut out = vec![0, 3];
    out.extend((data.len() as u16).to_be_bytes());
    out.extend(data);
    out
}
pub fn announce(clock: u64, seq: u16, ns: u64) -> Vec<u8> {
    let mut p = header(11, 76, clock, seq, 0x0408, 0);
    timestamp(&mut p[34..44], ns);
    // Local oscillator; priority is a preference, not a claim of GPS accuracy.
    p[47] = 96;
    p[48] = 248;
    p[49] = 0xfe;
    p[50..52].copy_from_slice(&0xffffu16.to_be_bytes());
    p[52] = 128;
    p[53..61].copy_from_slice(&clock.to_be_bytes());
    p[63] = 0xa0;
    p[64..68].copy_from_slice(&[0, 8, 0, 8]);
    p[68..].copy_from_slice(&clock.to_be_bytes());
    p
}
pub fn sync_pair(clock: u64, seq: u16, ns: u64) -> (Vec<u8>, Vec<u8>) {
    let mut sync = header(0, 44, clock, seq, 0x0608, -3);
    timestamp(&mut sync[34..], ns);
    let mut follow = header(8, 96, clock, seq, 0x0408, -3);
    timestamp(&mut follow[34..44], ns);
    let mut rate = vec![0; 28];
    rate[..6].copy_from_slice(&[0, 0x80, 0xc2, 0, 0, 1]);
    let mut cid = vec![0; 16];
    cid[..6].copy_from_slice(&[0, 0x0d, 0x93, 0, 0, 4]);
    cid[6..14].copy_from_slice(&clock.to_be_bytes());
    follow[44..76].copy_from_slice(&tlv(&rate));
    follow[76..].copy_from_slice(&tlv(&cid));
    (sync, follow)
}
pub struct PtpMaster {
    pub clock_id: u64,
    pub received: Arc<AtomicU64>,
    followed: Arc<Mutex<BTreeMap<IpAddr, ReceiverClock>>>,
    cancel: Cancellation,
    worker: Option<JoinHandle<()>>,
}
impl PtpMaster {
    pub fn start(peers: Vec<IpAddr>, follow_peers: Vec<IpAddr>, clock: Clock) -> Result<Self> {
        let event = UdpSocket::bind("0.0.0.0:319").context("PTP event port 319 unavailable")?;
        let general = UdpSocket::bind("0.0.0.0:320").context("PTP general port 320 unavailable")?;
        event.set_nonblocking(true)?;
        general.set_nonblocking(true)?;
        let clock_id = rand::random::<u64>() & 0x7fff_ffff_ffff_ffff;
        let received = Arc::new(AtomicU64::new(0));
        let rx = received.clone();
        let followed: Arc<Mutex<BTreeMap<_, _>>> = Arc::new(Mutex::new(follow_peers.into_iter()
            .map(|peer| (peer, ReceiverClock::default())).collect()));
        let receiver_clocks = followed.clone();
        let cancel = Cancellation::default();
        let stop = cancel.clone();
        let worker = thread::Builder::new()
            .name("airplay-ptp".into())
            .spawn(move || {
                let mut seq = 0u16;
                let mut last_announce = Instant::now() - Duration::from_secs(2);
                let mut next_sync = Instant::now();
                let mut buf = [0u8; 2048];
                while !stop.is_cancelled() {
                    let now = Instant::now();
                    if now >= next_sync {
                        for peer in &peers {
                            if receiver_clocks.lock().unwrap().contains_key(peer) { continue; }
                            let (sync, follow) = sync_pair(clock_id, seq, clock.now_ns());
                            let _ = event.send_to(&sync, (*peer, 319));
                            let _ = general.send_to(&follow, (*peer, 320));
                            if now.duration_since(last_announce) >= Duration::from_secs(1) {
                                let _ = general.send_to(
                                    &announce(clock_id, seq, clock.now_ns()),
                                    (*peer, 320),
                                );
                            }
                        }
                        if now.duration_since(last_announce) >= Duration::from_secs(1) {
                            last_announce = now;
                        }
                        seq = seq.wrapping_add(1);
                        next_sync = now + Duration::from_millis(125);
                    }
                    for socket in [&event, &general] {
                        while let Ok((n, addr)) = socket.recv_from(&mut buf) {
                            if !peers.contains(&addr.ip()) || n < 34 || buf[1] & 0xf != 2 {
                                continue;
                            }
                            let length = u16::from_be_bytes([buf[2], buf[3]]) as usize;
                            if length > n || length < 34 {
                                continue;
                            }
                            rx.fetch_add(1, Ordering::Relaxed);
                            if let Some(receiver) = receiver_clocks.lock().unwrap().get_mut(&addr.ip()) {
                                receiver.observe(&buf[..length], clock.now_ns());
                            }
                            let kind = buf[0] & 0xf;
                            let request_seq = u16::from_be_bytes([buf[30], buf[31]]);
                            if (kind == 1 || kind == 2) && n >= 44 {
                                let response_kind = if kind == 1 { 9 } else { 3 };
                                let mut response = header(
                                    response_kind,
                                    54,
                                    clock_id,
                                    request_seq,
                                    if kind == 1 { 0x0408 } else { 0x0608 },
                                    -3,
                                );
                                timestamp(&mut response[34..44], clock.now_ns());
                                response[44..54].copy_from_slice(&buf[20..30]);
                                if kind == 1 {
                                    let _ = general.send_to(&response, (addr.ip(), 320));
                                } else {
                                    let _ = event.send_to(&response, (addr.ip(), 319));
                                    let mut follow =
                                        header(10, 54, clock_id, request_seq, 0x0408, -3);
                                    timestamp(&mut follow[34..44], clock.now_ns());
                                    follow[44..54].copy_from_slice(&buf[20..30]);
                                    let _ = general.send_to(&follow, (addr.ip(), 320));
                                }
                            }
                        }
                    }
                    thread::sleep(Duration::from_millis(2));
                }
            })?;
        Ok(Self {
            clock_id,
            received,
            followed,
            cancel,
            worker: Some(worker),
        })
    }
    pub fn timeline(&self, peer: IpAddr, now_ns: u64) -> Option<(u64, u64)> {
        match self.followed.lock().unwrap().get(&peer) {
            Some(receiver) => receiver.sample(now_ns),
            None => Some((self.clock_id, now_ns)),
        }
    }
}
impl Drop for PtpMaster {
    fn drop(&mut self) {
        self.cancel.cancel();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn wire_clock_and_lengths() {
        let id = 0x1234567812345678;
        let (s, f) = sync_pair(id, 65535, 1234567890);
        assert_eq!(&s[20..28], &id.to_be_bytes());
        assert_eq!(s.len(), 44);
        assert_eq!(f.len(), 96);
        assert_eq!(&f[34..44], &[0, 0, 0, 0, 0, 1, 13, 251, 56, 210]);
        assert_eq!(announce(id, 0, 0).len(), 76);
    }
    #[test]
    fn receiver_clock_maps_paired_follow_up_with_correction() {
        let id = 0x1234567812345678;
        let local = 1_790_000_000_000_000_000;
        let mut clock = ReceiverClock::default();
        clock.observe(&announce(id, 1, 0), local);
        let (sync, mut follow) = sync_pair(id, 42, 2_000_000_000);
        follow[8..16].copy_from_slice(&(2000i64 * 65536).to_be_bytes());
        clock.observe(&sync, local);
        clock.observe(&follow, local + 10_000);
        assert_eq!(clock.sample(local + 100_000), Some((id, 2_000_102_000)));
        assert!(clock.sample(local + 3_000_000_001).is_none());
    }
    #[test]
    fn receiver_clock_rejects_unpaired_wrong_source_and_invalid_timestamps() {
        let id = 0x1234567812345678;
        let mut clock = ReceiverClock::default();
        let (sync, follow) = sync_pair(id, 42, 2_000_000_000);
        clock.observe(&follow, 10_000_000_000);
        assert!(clock.sample(10_000_000_000).is_none());
        clock.observe(&announce(id, 1, 0), 10_000_000_000);
        clock.observe(&sync, 10_000_000_000);
        for changed in [20, 30] {
            let mut invalid = follow.clone(); invalid[changed] ^= 1;
            clock.observe(&invalid, 10_000_000_001);
            assert!(clock.sample(10_000_000_001).is_none());
        }
        let mut invalid = follow.clone(); invalid[40..44].copy_from_slice(&1_000_000_000u32.to_be_bytes());
        clock.observe(&invalid, 10_000_000_001);
        assert!(clock.sample(10_000_000_001).is_none());
        clock.observe(&follow, 10_000_000_001);
        assert_eq!(clock.sample(10_000_000_001), Some((id, 2_000_000_001)));
        clock.observe(&announce(id + 1, 1, 0), 10_000_000_002);
        assert!(clock.sample(10_000_000_002).is_none());
    }
}
