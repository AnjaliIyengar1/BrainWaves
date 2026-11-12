# BrainWaves

A local end-to-end pipeline that simulates, parses, processes, and visualizes real-time brain-wave (EEG) data on a live web dashboard—allowing you to test streaming architecture without needing physical hardware.

## How it works

```
  +-----------------------+
  |   Simulator/          |  <-- Generates EEG values (64 ch, 1,000 samples/s)
  +-----------------------+
              |
              | TCP socket :5555  (100 packets/s)
              v
  +-------------------------------------------------------------+
  | DataProcessor/ (C# backend)                                 |
  |                                                             |
  |  [ TcpIngestService + FrameReader ]                         |
  |         |  Receives bytes, validates header + CRC,          |
  |         |  detects lost packets (uses Protocol/)            |
  |         v                                                   |
  |  [ Bounded Channel<T> ]  <-- Thread-safe queue, 1,000 slots |
  |         |                    Drops oldest on overflow       |
  |         v                                                   |
  |  [ ProcessingService ]   <-- Notch filter (removes 60 Hz),  |
  |                              keeps every 5th sample,        |
  |                              pushes 25 batches/s            |
  +-------------------------------------------------------------+
              |
              | SignalR / WebSocket :5236
              v
  +-------------------------------------------------------------+
  | web/ (React + TypeScript dashboard)                         |
  |                                                             |
  |  [ App.tsx ]                                                |
  |         |  Receives batches, writes to circular buffers     |
  |         |  (last 5 s per channel)                           |
  |         v                                                   |
  |  [ <canvas> ]            <-- Redraws at screen refresh      |
  |                              (~60 fps), independent of data |
  +-------------------------------------------------------------+

  Protocol/ = shared packet format, used by Simulator and DataProcessor
```

## Run

One command per terminal, from the `BrainWaves` folder:

```bash
# Terminal 1 (start first; note the "Now listening on" port)
dotnet run --project DataProcessor

# Terminal 2
dotnet run --project Simulator

# Terminal 3
cd web
npm run dev
```

Then open the URL Terminal 3 prints (usually `http://localhost:5173`).

| Port | Used by | Carries |
|---|---|---|
| 5555 | Simulator → DataProcessor | Raw binary packets (TCP) |
| 5236 | Web page → DataProcessor | HTTP + WebSocket (SignalR `/hubs/brain`) |

## Packet format (v1, little-endian)

```
 Whole packet
+-------------+------------------------------------+----------+
|   Header    |        Brain data (payload)        |   CRC    |
|  28 bytes   |  N = channels x samples x 4 bytes  | 4 bytes  |
+-------------+------------------------------------+----------+
 byte 0        byte 28                              byte 28+N
```

## Throughput (default config)

```
Rate        = 1,000 samples/s per channel
Per ms      = 64 ch × 1 sample      = 64 values
Per packet  = 64 × 10 samples       = 640 values
Payload     = 640 × 4 B (float32)   = 2,560 B
Packet      = 2,560 + 28 + 4        = 2,592 B   (data + header + CRC)
Packets/s   = 1,000 / 10            = 100
Throughput  = 100 × 2,592           = 259,200 B/s ≈ 259 KB/s
```

Trade-off: more samples per packet = less header overhead, but more latency (10 samples = 10 ms delay).

## Simulated signal (`Simulator/SignalGenerator.cs`)

The signal is randomly generated. All 64 channels use the same mix, each with its own random strength and timing:

| Component | Frequency | Amplitude | Purpose |
|---|---|---|---|
| Alpha wave | 10 Hz | 10-30 uV (random per channel) | Brain's "relaxed" rhythm |
| Beta wave | 20 Hz | 2-8 uV (random per channel) | "Active thinking" rhythm |
| Line noise | 60 Hz | 5 uV (same on all channels) | US power-line hum; added on purpose so DataProcessor has something to filter out |
| Random noise | - | ~2 uV std dev (Gaussian) | Real sensors are never clean |

Units are microvolts (uV). Pass a `seed` for a repeatable signal in tests.

`Sequence` is the packet number. It never resets while the app runs:

```
Packet 0 → moments 0–9
Packet 1 → moments 10–19
Packet 2 → moments 20–29
```

It only starts again from 0 when the app restarts.

## DataProcessor (receiver)

```
Simulator → TcpIngestService (receive + check) → queue (Channel) → ProcessingService (filter) → SignalR → web page
```

- `TcpIngestService`: the "front door"; accepts device connections on port 5555.
- Queue: `Channel<FrameMessage>`, created in `Program.cs`.
- `ProcessingService`: removes 60 Hz hum (`NotchFilter`), keeps every 5th sample, pushes to the web page 25 times/s.

Notch filter: removes one narrow frequency (60 Hz power-line hum) and keeps the rest.

```
Before:  ~~~~10Hz~~~~20Hz~~~~~~~~60Hz(hum)~~~~
After:   ~~~~10Hz~~~~20Hz~~~~~~~~ \/ ~~~~~~~~~   ← 60 Hz cut out
```

Reads packets, checks them, and catches lost ones (`DataProcessor/FrameReader.cs`):

1. Reads 28 bytes (the header).
2. If they aren't a valid header, slides forward 1 byte and tries again (resync).
3. Works out the packet size and reads the rest.
4. Checks the CRC. If it's bad, drops the packet and counts the error.
5. Checks the sequence. A jump means lost packets; going backwards means a new session.
