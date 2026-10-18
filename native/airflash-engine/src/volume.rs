//! Device volume is receiver state, independent of PCM gain. All RTSP I/O stays
//! off the media thread and shares the feedback connection's transaction lock.
use crate::rtsp::{Cancellation, Connection};
use anyhow::{Context, Result, ensure};
use serde_json::{Value, json};
use std::{
    sync::{Arc, Mutex, mpsc},
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

#[derive(Clone, Copy, Debug)]
pub struct Command {
    pub sequence: u64,
    pub percent: u8,
}
#[derive(Clone, Default)]
pub struct Control(Arc<Mutex<Option<Command>>>);
impl Control {
    pub fn set(&self, command: Command) {
        let mut latest = self.0.lock().unwrap();
        if latest.is_none_or(|old| command.sequence > old.sequence) {
            *latest = Some(command);
        }
    }
    fn latest(&self) -> Option<Command> {
        *self.0.lock().unwrap()
    }
}
pub type Peer = (String, String, Arc<Mutex<Connection>>);
pub fn to_db(percent: u8) -> f64 {
    if percent == 0 {
        -144.0
    } else {
        f64::from(percent) * 0.3 - 30.0
    }
}
pub fn from_db(db: f64) -> Result<u8> {
    ensure!(db.is_finite() && db <= 0.0, "invalid receiver volume");
    Ok(if db <= -30.0 {
        0
    } else {
        ((db + 30.0) / 0.3).round() as u8
    })
}
pub(crate) fn read(connection: &mut Connection) -> Result<u8> {
    let info = connection.request("GET", "/info", &[], &[])?.plist()?;
    let value = info
        .as_dictionary()
        .and_then(|d| d.get("initialVolume"))
        .context("receiver does not report initialVolume")?;
    let db = value
        .as_real()
        .or_else(|| value.as_signed_integer().map(|n| n as f64))
        .context("invalid initialVolume type")?;
    from_db(db)
}
#[derive(Default)]
struct Pending {
    command: Option<Command>,
    sent: Option<Instant>,
    write_failed: bool,
    settled: bool,
}
impl Pending {
    fn status(&mut self, values: &[Option<u8>], now: Instant) -> &'static str {
        let readable = values.iter().all(Option::is_some);
        if let Some(command) = self.command {
            if !self.settled {
                if !self.write_failed
                    && readable
                    && values.iter().all(|v| *v == Some(command.percent))
                {
                    self.settled = true;
                    return "confirmed";
                }
                if !self.write_failed
                    && self
                        .sent
                        .is_some_and(|at| now.duration_since(at) < Duration::from_secs(3))
                {
                    return "pending";
                }
                self.settled = true;
                return "unconfirmed";
            }
        }
        if readable { "confirmed" } else { "unsynced" }
    }
}
pub struct Worker {
    stop: Cancellation,
    worker: Option<JoinHandle<()>>,
    pub events: mpsc::Receiver<Value>,
}
impl Worker {
    /// Peers are ordered with the stereo leader first by the desktop controller.
    pub fn start(peers: Vec<Peer>, control: Control) -> Result<Self> {
        ensure!(!peers.is_empty(), "volume worker requires a receiver");
        let stop = Cancellation::default();
        let done = stop.clone();
        let (tx, events) = mpsc::sync_channel(32);
        let worker = thread::Builder::new().name("airplay-volume".into()).spawn(move || {
            let mut pending = Pending::default();
            let mut next = Instant::now();
            while !done.is_cancelled() {
                let command = control.latest();
                let changed = command.map(|c| c.sequence) != pending.command.map(|c| c.sequence);
                if !changed && Instant::now() < next {
                    thread::sleep(Duration::from_millis(20));
                    continue;
                }
                let mut errors = Vec::new();
                if changed {
                    pending = Pending { command, sent: Some(Instant::now()), ..Pending::default() };
                    if let Some(command) = command {
                        for (host, uri, connection) in &peers {
                            if done.is_cancelled() { return; }
                            let result = connection.lock().unwrap().request("SET_PARAMETER", uri,
                                &[("Content-Type", "text/parameters".into())],
                                format!("volume: {:.6}\r\n", to_db(command.percent)).as_bytes());
                            if let Err(error) = result {
                                pending.write_failed = true;
                                errors.push(format!("{host}: {error:#}"));
                            }
                        }
                    }
                }
                let mut values = Vec::new();
                for (host, _, connection) in &peers {
                    if done.is_cancelled() { return; }
                    match read(&mut connection.lock().unwrap()) {
                        Ok(value) => values.push(Some(value)),
                        Err(error) => { values.push(None); errors.push(format!("{host}: {error:#}")); }
                    }
                }
                let status = pending.status(&values, Instant::now());
                let event = json!({"event":"device_volume", "host":peers[0].0,
                    "sequence":pending.command.map_or(0, |c| c.sequence),
                    "volume":values[0], "available":values.iter().all(Option::is_some),
                    "status":status, "message":errors.join("; "),
                    "members":peers.iter().zip(&values).map(|((host,_,_),v)| json!({"host":host,"volume":v})).collect::<Vec<_>>()});
                let _ = tx.try_send(event);
                next = Instant::now() + if status == "pending" { Duration::from_millis(100) } else { Duration::from_secs(1) };
            }
        })?;
        Ok(Self {
            stop,
            worker: Some(worker),
            events,
        })
    }
}
impl Drop for Worker {
    fn drop(&mut self) {
        self.stop.cancel();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn pending_readback_confirms_within_750ms_and_idle_polling_stays_slow() {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let connection = Connection::connect(listener.local_addr().unwrap(), Cancellation::default()).unwrap();
        let (socket, _) = listener.accept().unwrap();
        let stop = Cancellation::default();
        let done = stop.clone();
        let server = thread::spawn(move || {
            let mut connection = Connection::from_stream(socket, done.clone()).unwrap();
            let mut volume = 15;
            let mut delayed = None;
            while !done.is_cancelled() {
                let request = match connection.read_for(Duration::from_millis(20), None) {
                    Ok(Some(request)) => request,
                    Ok(None) => continue,
                    Err(_) => break,
                };
                let mut body = Vec::new();
                if request.first.starts_with("GET /info ") {
                    let mut info = plist::Dictionary::new();
                    info.insert("initialVolume".into(), plist::Value::Real(to_db(volume)));
                    plist::Value::Dictionary(info).to_writer_binary(&mut body).unwrap();
                    if let Some(next) = delayed.take() { volume = next; }
                } else {
                    assert!(request.first.starts_with("SET_PARAMETER "));
                    let text = std::str::from_utf8(&request.body).unwrap();
                    delayed = Some(from_db(text.trim().strip_prefix("volume: ").unwrap().parse().unwrap()).unwrap());
                }
                let mut response = format!("RTSP/1.0 200 OK\r\nCSeq: {}\r\nContent-Length: {}\r\n\r\n", request.headers["cseq"], body.len()).into_bytes();
                response.extend(body);
                if connection.write(&response).is_err() { break; }
            }
        });
        let control = Control::default();
        let worker = Worker::start(vec![("127.0.0.1".into(), "rtsp://127.0.0.1/volume".into(), Arc::new(Mutex::new(connection)))], control.clone()).unwrap();
        let result = (|| -> Result<()> {
            let initial = worker.events.recv_timeout(Duration::from_secs(2))?;
            ensure!(initial["volume"] == 15 && initial["sequence"] == 0, "initial GET must report the unchanged receiver volume");
            for (sequence, old, target) in [(1, 15, 14), (2, 14, 15)] {
                let started = Instant::now();
                control.set(Command { sequence, percent: target });
                let pending = worker.events.recv_timeout(Duration::from_millis(250)).context("a new command must wake the idle worker promptly")?;
                ensure!(pending["sequence"] == sequence && pending["volume"] == old && pending["status"] == "pending", "first GET after SET must still expose the old volume");
                let remaining = Duration::from_millis(750).saturating_sub(started.elapsed());
                let confirmed = worker.events.recv_timeout(remaining).context("the next GET must confirm the asynchronous write within 750ms")?;
                ensure!(confirmed["sequence"] == sequence && confirmed["volume"] == target && confirmed["status"] == "confirmed", "a fresh GET must confirm the matching command");
                println!("sequence {sequence}: stale GET then confirmed in {}ms", started.elapsed().as_millis());
                ensure!(matches!(worker.events.recv_timeout(Duration::from_millis(250)), Err(mpsc::RecvTimeoutError::Timeout)), "settled polling must keep its one-second cadence");
            }
            Ok(())
        })();
        stop.cancel();
        drop(worker);
        server.join().unwrap();
        result.unwrap();
    }
    #[test]
    fn conversion_and_invalid_values() {
        for percent in 0..=100 {
            assert_eq!(from_db(to_db(percent)).unwrap(), percent);
        }
        assert_eq!(from_db(-144.0).unwrap(), 0);
        assert!(from_db(f64::NAN).is_err());
        assert!(from_db(f64::INFINITY).is_err());
        assert!(from_db(1.0).is_err());
    }
    #[test]
    fn delayed_reads_and_pair_confirmation() {
        let now = Instant::now();
        let mut pending = Pending {
            command: Some(Command {
                sequence: 1,
                percent: 40,
            }),
            sent: Some(now),
            ..Pending::default()
        };
        assert_eq!(pending.status(&[Some(20), Some(20)], now), "pending");
        assert_eq!(pending.status(&[Some(40), Some(20)], now), "pending");
        assert_eq!(pending.status(&[Some(40), Some(40)], now), "confirmed");
        // External physical changes are authoritative once the write is confirmed.
        assert_eq!(pending.status(&[Some(45), Some(45)], now), "confirmed");
    }
    #[test]
    fn timeout_and_missing_reads_do_not_confirm() {
        let now = Instant::now();
        let mut pending = Pending {
            command: Some(Command {
                sequence: 1,
                percent: 40,
            }),
            sent: Some(now),
            ..Pending::default()
        };
        assert_eq!(pending.status(&[None], now), "pending");
        assert_eq!(
            pending.status(&[Some(20)], now + Duration::from_secs(3)),
            "unconfirmed"
        );
        assert_eq!(
            pending.status(&[None], now + Duration::from_secs(4)),
            "unsynced"
        );
    }
    #[test]
    fn latest_command_wins() {
        let control = Control::default();
        control.set(Command {
            sequence: 2,
            percent: 30,
        });
        control.set(Command {
            sequence: 1,
            percent: 80,
        });
        assert_eq!(control.latest().unwrap().percent, 30);
    }
}
