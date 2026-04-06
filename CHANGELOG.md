StrokerSync Update v4 — Signal & Connection Improvements
Sync & Responsiveness

Adaptive send rate: Command frequency now scales dynamically with motion speed. Fast strokes use the full configured rate; slow/idle movement backs off to 5 Hz, reducing unnecessary Bluetooth traffic. The "Send Rate" slider now sets the maximum rate.
Accumulator-based timing: Replaced the simple timestamp check with a time accumulator that carries leftover frame time forward. The average command rate now stays mathematically on-target regardless of frame rate fluctuations. Capped at 2x interval to prevent burst-sending after alt-tab or loading stalls.
Predictive LinearCmd duration: The device interpolation duration is now calculated from the actual time until the next expected command, plus a small user-configurable padding. Previously a fixed value, this caused the device to either sit idle between commands (at low rates) or never complete a movement (at high rates). The "Device Smoothness" slider is now "Duration Padding" (default 5ms).
Source-space deadband: Position deadband is now applied before stroke zone mapping. Previously, a compressed stroke zone (e.g. 0.3–0.7) inflated the effective deadband by up to 2.5x.
Penetration hysteresis: The "no penetration" threshold now uses hysteresis (stop at 0.005, resume at 0.02) to prevent the device from rapidly toggling on/off when the tip barely touches the target.

Tracking & Filtering

Conditional spike rejection: The median filter no longer runs unconditionally. During smooth motion, raw samples pass through with zero added latency. The median only engages when a sample deviates beyond a spike threshold, limiting the 1-frame latency penalty to actual physics glitches.
Asymmetric EMA: The noise filter now smooths direction-dependently. Increasing penetration depth gets 4x less smoothing for snappy attack feel; withdrawal uses full smoothing for softer release.
Pelvis-blended vaginal direction: Vaginal canal direction now blends 70% trigger-based vector with 30% pelvis bone axis. This stabilizes depth tracking during extreme poses where the labia/vagina triggers alone give a poor canal estimate. Falls back to trigger-only if no pelvis bone is found.

Connection & Reliability

WebSocket fragmented frame support: TinyWebSocket now handles continuation frames (opcode 0x0) per RFC 6455 §5.4. Control frames (ping/pong) are processed immediately even mid-fragment.
Callback TTL cleanup: Pending Buttplug protocol callbacks now expire after 10 seconds. A sweep runs every 5 seconds to prevent unbounded memory growth during long sessions if the server silently drops responses.
Sampled LinearCmd error logging: Every 100th position command now registers an error callback. This catches persistent device errors without flooding the callback system (~one check every 3 seconds at 30 Hz).
Clean client disposal on reconnect: The old ButtplugClient is now explicitly disconnected before creating a new one during auto-reconnect, preventing ghost receive threads from accumulating.
Component cache validation: Cached rigidbody/transform references are verified every 2 seconds. If an atom reload (clothing change, appearance preset) silently destroys a component, the cache is rebuilt immediately rather than waiting for a full null-out failure.