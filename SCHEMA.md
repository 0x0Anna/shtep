# SimHub → TSV Telemetry Export Schema (v1.1)

Plain-text, tool-agnostic intermediate format. No proprietary markers embedded in
the data — a file's existence and its start/end timestamps in the sidecar *are*
the session boundary. Any TSV-capable tool (Excel, pandas, awk) can open the data
file directly with zero knowledge of this spec.

This file pair is also the input contract for shtep's own optional MoTeC
`.ld` exporter (`src/TelemetryExportPlugin/Export/`, gated behind the
`ExportMotecLd` setting) — it reads a finished pair back exactly the way
any external converter would, after `Close()` has moved both files to
`OutputDir`.

## File pair, per recording

Every recording produces **two files with the same base name**:

```
{base}.tsv          — data, written incrementally during recording
{base}.meta.json    — sidecar, written once on close
```

`{base}` naming convention:

```
{sim}_{context}_{yyyyMMdd_HHmmss}
```

- `sim`: `rbr`, `dirt`, `acr`, `eawrc`, `acc`, `ac`, `iracing`, etc. (SimHub's own game id)
- `context`: stage name (rally) or track name (circuit), sanitized (`[A-Za-z0-9_-]` only, spaces → `_`)
- Timestamp: recording **start** time, local time, plugin host clock

Example: `rbr_maantie1_20260727_143205.tsv` / `.meta.json`

## Recording trigger

A config toggle (`RecordingTrigger`, default `Automatic`) selects what opens
and closes a session:

- **`Automatic`** (default): the write lifecycle below — stage-start/end
  (rally) or pit-exit/entry (circuit, debounced).
- **`SimHubRecording`**: session start/end instead follows SimHub's own
  record toggle, read from `StatusDataBase.ReplayMode` (a plain `string`
  property, confirmed by reflecting `GameReaderCommon.dll`) — observed live to
  take exactly the values `"Record"` / `"Live"`, tracking SimHub's record
  toggle every tick. Unlike an earlier attempt at this (edge-triggering off
  `DataCorePlugin.LoggingLastMessage`, SimHub's transient most-recent log
  line), `ReplayMode` is a persistent per-tick property, so the session
  start/end just follows its current value directly — no latch needed. In
  this mode CircuitBoundary/RallyBoundary are not fed and never open/close a
  session themselves.

## Write lifecycle

1. On stage-start (rally) or pit-exit (circuit, debounced) — or, in
   `SimHubRecording` trigger mode, on SimHub's own record-start: open
   `{TempDir}/{base}.tsv.partial`, write the header row immediately.
2. Stream one row per sample tick (see Sampling below) as telemetry arrives.
3. On stage-end (rally), pit-entry (circuit, debounced), **or the game
   disconnecting mid-session (any trigger mode)** — all of the above only run
   from inside the per-tick data callback, which stops seeing real telemetry
   the instant the game disconnects, so disconnect itself is treated as an
   implicit end rather than leaving the session open until the plugin/SimHub
   shuts down:
   - Close the data file.
   - Write `{TempDir}/{base}.meta.json`.
   - Move the `.meta.json` **first**, then rename/move `.tsv.partial` → `.tsv`
     **last**, both landing in `OutputDir`. A converter treats the sidecar's
     arrival as the "recording is complete" signal — never the data file's
     presence alone. If the converter ever sees a sidecar with no matching
     `.tsv` yet, it just waits and retries; it never has to guess whether a
     lone `.tsv` is finished.
   - If `TempDir` and `OutputDir` are on different volumes, copy-then-delete
     instead of rename, but the ordering guarantee above still holds.
4. `TempDir` and `OutputDir` are independently configurable in plugin settings.
   **Validate both at config-save time** (see Startup & config validation
   below), not just at first recording attempt.

### Crash recovery / orphaned `.partial` files

On plugin startup, scan `TempDir` for leftover `.tsv.partial` files from a
prior crash (SimHub crash, plugin exception, power loss, etc.):

- **Default behavior — clean close:** truncate the file to the last complete
  row (detect a partially-written final line by checking it ends in `\n` and
  has the expected column count; drop it if not), then write a sidecar with
  `"recoveredFromCrash": true` and `"endTimeUtc"` set to the last row's own
  timestamp rather than a real stop event, and move the pair to `OutputDir`
  as usual.
- **Plugin option — purge on recovery:** a config toggle
  (`PurgeIncompleteOnStartup: bool`, default `false`) that deletes orphaned
  `.partial` files instead of salvaging them.
- This only runs once at startup, before any new recording opens — it never
  touches a `.partial` file actively being written this session.

A converter watching `OutputDir` only needs to react to a data file appearing
*after* its matching sidecar exists — the `.partial` suffix, separate temp
directory, and sidecar-first ordering together guarantee it never picks up a
file mid-write or mid-crash-recovery.

## Sampling

Fixed-rate, not event-driven. Sample on SimHub's data update tick at a
**configurable rate** (default 100 Hz). Hold last-known value for any channel
that hasn't updated since the previous tick. This keeps every channel at constant
frequency going into the file, so the Rust-side `.ld` writer doesn't need to
resample — MoTeC channels are expected to be fixed-frequency.

### Pause handling

Every data row includes a `Paused` column (`0`/`1`) — always present, not
conditional — so no row ever needs a non-numeric sentinel to represent state.

- While the sim reports paused (alt-tab, menu, replay, etc.), the normal
  fixed-rate sample cadence **stops** — no rows are written during the pause.
- Exactly one row is written at the pause-**enter** transition
  (`Paused=1`, other columns hold last-known values) and one at the
  pause-**exit** transition (`Paused=0`, resuming normal telemetry). Both are
  ordinary rows, same column layout as every other row — just flagged.
- This keeps `LapDistance`/timing-sensitive columns from accumulating garbage
  values through a pause, while still leaving a visible marker of when a pause
  happened and how long it lasted (via the two rows' `Time_s` gap).

### Discontinuity handling (resets, call-for-help, fast-travel)

Distinct from pause: events like RBR's reset-to-track, "call for help"
recovery, or open-world fast-travel (Forza Horizon) keep real stage/session
time elapsing — unlike pause, the clock should **not** stop and normal sample
cadence **continues throughout**. What breaks is continuity in
position/distance-derived channels: `LapDistance` and `PosX/Y/Z` can jump or
go backward across the event, and anything computing speed-from-position or
assuming monotonic distance will produce a spurious spike at that boundary if
it isn't told to skip it.

- **`Discontinuity` column** (`0`/`1`), always present, same shape as
  `Paused`. Meaning: "do not compute any distance/position-derived delta
  across this sample and the previous one." Set for the full duration of the
  affected window (e.g. the stationary wait during a call-for-help sequence,
  if the sim exposes that state) — sampling never stops for this reason, only
  flags.
- **Detection, two tiers** (config: `DiscontinuityDetection`:
  `"SimEvent" | "Heuristic" | "Both"`, default `"Both"`):
  1. **Sim-exposed event**, where the SimHub adapter provides one (e.g. an
     explicit resetting/awaiting-assist state). Verify per-sim — rally
     adapters may expose this; Forza's UDP telemetry may not.
  2. **Heuristic fallback**, required regardless of tier 1's availability:
     compute implied speed from the `LapDistance`/position delta between
     consecutive samples at the known sample interval; if it exceeds a
     configurable implausible-speed threshold
     (`HeuristicDiscontinuitySpeedKmh`, default ~400), flag that boundary
     retroactively as `Discontinuity=1`.
- **Sidecar gets an optional `discontinuities` array** for human-readable
  summary, kept out of the per-row numeric schema:
  ```json
  "discontinuities": [
    { "startTimeS": 42.10, "endTimeS": 47.35, "reason": "assist" }
  ]
  ```
  `reason` is one of `"reset"`, `"assist"`, `"fast_travel"`, `"unknown"` (the
  last one covers heuristic-only detections where the sim gave no signal as
  to which kind of event it was).

## Data file format (`{base}.tsv`)

- Tab-separated, one header row, then one row per sample.
- `Time_s`: first column always, elapsed seconds since file open, `%.3f`
  (or finer if your sample rate needs it), monotonic from 0.000.
- Remaining columns: configurable subset of the canonical channel list below,
  in whatever order the plugin config specifies — **but always using the exact
  header name from the table**, so the Rust converter matches by name, not
  position.
- Missing/unavailable channel for a given sim: omit the column entirely rather
  than filling it with a sentinel. The converter treats "column absent" and
  "channel absent" as the same thing.
- No comment lines, no blank lines, no embedded metadata — that all lives in
  the sidecar.
- **Encoding: UTF-8, no BOM. Line endings: `\n`.** Stated explicitly so
  neither side has to guess or auto-detect.
- **Number formatting: `CultureInfo.InvariantCulture` for every numeric
  field, no exceptions.** A host machine with a comma-decimal locale will
  otherwise silently corrupt every float column.

### Canonical channel table (extend as needed, keep names stable once shipped)

| Header            | Unit   | Notes                                   |
|-------------------|--------|------------------------------------------|
| `Paused`          | 0/1    | Always present; see Pause handling above |
| `Discontinuity`   | 0/1    | Always present; see Discontinuity handling above |
| `Speed_kmh`       | km/h   |                                          |
| `RPM`             | rpm    |                                          |
| `Gear`            | —      | -1 = reverse, 0 = neutral                |
| `Throttle_pct`    | 0–100  |                                          |
| `Brake_pct`       | 0–100  |                                          |
| `Clutch_pct`      | 0–100  |                                          |
| `SteerRatio`      | −1..1  | normalized fraction of full lock, AssettoCorsaRally only. Confirmed live (not degrees - values clip flat at exactly ±1.0); **sign convention unconfirmed** - don't assume + = either direction until a live full-lock test notes the actual direction at the time |
| `LapDistance_m`   | m      | distance into stage/lap                 |
| `LapDistancePct`  | 0–100  | alt. position axis, if sim exposes it    |
| `PosX_m`/`PosY_m`/`PosZ_m` | m | world-space, if sim exposes it   |
| `SuspTravelFL_mm` etc. (FL/FR/RL/RR) | mm | if sim exposes it     |
| `TyreTemp*` etc.  | °C     | per-corner, if sim exposes it            |
| `LatAccel_g`      | g      | ISO seat-frame lateral (sway)             |
| `LongAccel_g`     | g      | ISO seat-frame longitudinal (surge)       |
| `VertAccel_g`     | g      | ISO seat-frame vertical (heave)           |
| `ABSActive`       | 0/1    |                                          |
| `AirTemp_C`       | °C     |                                          |
| `TrackTemp_C`     | °C     |                                          |
| `FuelLevel_pct`   | 0–100  |                                          |
| `LapNumber`       | —      | circuit only, absent in rally files      |

Add new rows here (and only here) as channels are needed — don't rename
existing headers once a converter depends on them; add a new column instead.

### Rewind handling (Forza Horizon and similar rewind-capable games)

Different in kind from pause and discontinuity: rewind doesn't just introduce
a gap or a jump to account for — it invalidates rows **already written**,
since the player is discarding that stretch of the run in favor of a redo.
Flagging alone would leave two overlapping traces of the same track segment
in one file, which nothing downstream (MoTeC lap analysis, `sde-app`) has a
concept of resolving. Default behavior is therefore **truncation**, not
flagging.

- **`RewindHandling` config**: `"Truncate"` (default) | `"FlagOnly"` (keep
  everything and treat the boundary like a `Discontinuity` instead — useful
  if you ever want to review the pre-correction attempt, at the cost of the
  same overlapping-trace ambiguity noted above).
- **Detection** reuses the discontinuity heuristic (position/distance delta
  between consecutive samples) — a rewind's signature is specifically a
  **backward** jump along track position/distance, as opposed to resets and
  assists which move forward. Direction is the distinguishing signal between
  the two event types.
- **Truncation mechanics:** maintain a lightweight in-memory index while
  recording — `(position or LapDistance, byteOffset)` per row (or every Nth
  row) — small overhead, a few bytes per entry. On rewind detection: find the
  nearest prior index entry matching where the game landed, flush the
  writer, reopen the file read/write, seek to that byte offset, truncate
  (`SetLength`), trim the in-memory index to match, and resume writing
  forward from there. **Key the index on position (`PosX/Y/Z`) where the sim
  exposes it, not `LapDistance`** — `LapDistance` resets at lap boundaries,
  which would confuse the match if a rewind spans a lap crossing. Fall back
  to `LapDistance` only for sims without position data; a known limitation,
  not solved further in v1.
- **`Time_s` needs no special handling** — it's a synthetic counter
  (`row_index / SampleRateHz`), not wall-clock time, so it simply continues
  from wherever it was at the truncation point once writing resumes. No need
  to account for how long the rewind itself took in real time.
- **Sidecar gets an optional `rewinds` array**, since the discarded rows
  themselves no longer exist in the data file:
  ```json
  "rewinds": [
    { "truncatedFromTimeS": 58.20, "truncatedToTimeS": 31.00, "rowsRemoved": 2720 }
  ]
  ```

## Sidecar metadata (`{base}.meta.json`)

```json
{
  "schemaVersion": 1,
  "sim": "rbr",
  "sessionType": "stage",
  "context": "Maantie 1",
  "car": "Ford Escort MkII",
  "driver": "Annalise",
  "startTimeUtc": "2026-07-27T14:32:05Z",
  "endTimeUtc": "2026-07-27T14:38:41Z",
  "sampleRateHz": 100,
  "channels": ["Speed_kmh", "RPM", "Gear", "Throttle_pct", "Brake_pct", "..."],
  "discontinuities": [
    { "startTimeS": 42.10, "endTimeS": 47.35, "reason": "assist" }
  ],
  "rewinds": [
    { "truncatedFromTimeS": 58.20, "truncatedToTimeS": 31.00, "rowsRemoved": 2720 }
  ],
  "pluginVersion": "0.1.0"
}
```

- `schemaVersion` is the compatibility contract between the two repos — bump it
  whenever the TSV column semantics or file lifecycle change, not for additive
  channel-table entries.
- **Compatibility policy: backward-compatible by intent.** If the Rust
  converter's supported version is newer than a file's `schemaVersion`, it
  should still attempt to parse rather than hard-reject — treat unknown/absent
  fields as "not present in this file" rather than an error. Only refuse to
  parse if `schemaVersion` is *newer* than what the converter understands
  (i.e., the file uses a contract the converter hasn't been taught yet). This
  means schema changes should themselves be additive where possible (new
  optional fields, new channel-table rows) rather than renaming or repurposing
  existing ones, since that's what makes the backward-compat promise honest.
- `sessionType`: `"stage"` (rally, whole file = one stage) or `"stint"`
  (circuit, pit-exit to pit-entry).
- `channels`: exact list/order of data columns actually present in the paired
  `.tsv`, so the converter doesn't need to sniff the header to know what to
  expect — though it should still match by name against the header, not trust
  this list blindly.
- `discontinuities` and `rewinds` are both optional (omit entirely if none
  occurred during the recording) — human-readable summaries of what the
  `Discontinuity` column and any mid-recording truncation already encode in
  the data itself. Never the source of truth for parsing; the `.tsv` is.

## Startup & config validation

`TempDir` and `OutputDir` are checked at the point they're set in plugin
config (not deferred to first recording attempt):

- If a path doesn't exist, attempt to create it.
- If creation fails (invalid drive, no permissions, path on unavailable
  network share, etc.), surface a clear warning in the SimHub settings UI
  immediately, at config-save time, so the problem is caught before the
  first recording ever silently fails to start.
- Also re-validate both paths on plugin startup (drives can disappear
  between sessions — USB drive unplugged, network share unmounted — even if
  they were valid when configured).

## What's deliberately left out of v1

- No lap/marker offsets inside circuit files — decided per-stint files instead
  of one session file with embedded markers, so no marker bookkeeping needed
  in the plugin.
- No compression — plain text, let the filesystem/converter handle it if size
  becomes a problem.
- No binary framing — buffered `StreamWriter` at text-row rates (100Hz,
  ~20-40KB/sec) is nowhere near a throughput ceiling; revisit only if actual
  measurements say otherwise, and only as framing, not as an external
  database — a DB adds a network hop and a dependency to the recording path
  without solving a bottleneck that doesn't exist yet at these rates.
- No fan-out/pub-sub — if a future need arises for other tools to consume the
  live stream concurrently (not just "TSV writes are slow"), that's a
  legitimately different problem (e.g. Redis pub/sub) and worth solving
  separately from this file-based handoff, not folded into it.
