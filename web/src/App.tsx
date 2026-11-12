import { useEffect, useRef, useState } from "react";
import * as signalR from "@microsoft/signalr";

type SignalBatch = {
  sequence: number;
  timestampMicros: number;
  channels: number;
  sampleRateHz: number;
  samples: number[]; // sample-major: samples[s * channels + c]
};

const SHOWN_CHANNELS = 8;
const WINDOW_SECONDS = 5;
const DISPLAY_RATE_HZ = 200;            // must match backend (1000 / 5)
const POINTS = WINDOW_SECONDS * DISPLAY_RATE_HZ;
const ROW_RANGE_UV = 60;                // each row shows -60..+60 uV

export default function App() {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  // Circular buffer per channel: newest point overwrites oldest.
  const buffers = useRef(Array.from({ length: SHOWN_CHANNELS }, () => new Float32Array(POINTS)));
  const writeIndex = useRef(0);
  const [status, setStatus] = useState("connecting");
  const [lastSeq, setLastSeq] = useState(0);

  // Receive data.
  useEffect(() => {
    let cancelled = false; // React dev mode runs effects twice
    const conn = new signalR.HubConnectionBuilder()
      .withUrl("/hubs/brain")
      .withAutomaticReconnect()
      .build();

    conn.on("signal", (b: SignalBatch) => {
      const n = b.samples.length / b.channels;
      const shown = Math.min(SHOWN_CHANNELS, b.channels);
      for (let s = 0; s < n; s++) {
        const i = writeIndex.current;
        for (let c = 0; c < shown; c++) {
          buffers.current[c][i] = b.samples[s * b.channels + c];
        }
        writeIndex.current = (i + 1) % POINTS;
      }
      setLastSeq(b.sequence);
    });

    conn.onreconnecting(() => !cancelled && setStatus("reconnecting"));
    conn.onreconnected(() => !cancelled && setStatus("connected"));
    conn.onclose(() => !cancelled && setStatus("disconnected"));
    conn.start()
      .then(() => !cancelled && setStatus("connected"))
      .catch((e) => !cancelled && setStatus(`error: ${e}`));

    return () => {
      cancelled = true;
      conn.stop();
    };
  }, []);

  // Draw at screen refresh rate, independent of data arrival.
  useEffect(() => {
    let raf = 0;
    const draw = () => {
      const canvas = canvasRef.current;
      const ctx = canvas?.getContext("2d");
      if (canvas && ctx) {
        const { width, height } = canvas;
        const rowH = height / SHOWN_CHANNELS;
        const xStep = width / (POINTS - 1);
        const scale = rowH / 2 / ROW_RANGE_UV;
        const oldest = writeIndex.current;

        ctx.clearRect(0, 0, width, height);
        ctx.lineWidth = 1;
        ctx.strokeStyle = "#378ADD";
        ctx.fillStyle = "#666";

        for (let c = 0; c < SHOWN_CHANNELS; c++) {
          const buf = buffers.current[c];
          const mid = rowH * (c + 0.5);
          ctx.beginPath();
          for (let p = 0; p < POINTS; p++) {
            const y = mid - buf[(oldest + p) % POINTS] * scale;
            if (p === 0) ctx.moveTo(0, y);
            else ctx.lineTo(p * xStep, y);
          }
          ctx.stroke();
          ctx.fillText(`ch${c}`, 4, rowH * c + 12);
        }
      }
      raf = requestAnimationFrame(draw);
    };
    raf = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(raf);
  }, []);

  return (
    <div style={{ padding: 16, fontFamily: "sans-serif" }}>
      <h2>BrainWaves live</h2>
      <p>Status: {status} | Last packet: {lastSeq}</p>
      <canvas ref={canvasRef} width={1000} height={600} style={{ border: "1px solid #ccc" }} />
    </div>
  );
}