# Pi Toolbox export research (`.ibt`)

Status as of **2026-08-07**: format problem **fully solved and validated end to end**.
Files generated from real shtep `.tsv` recordings load in Cosworth Pi Toolbox with
correct metadata, telemetry traces, and — for circuit sessions — a working **lap
selector**. Both the rally (single-stage) and circuit (multi-lap) paths are confirmed.

Nothing is implemented in the plugin yet. This document is the handoff so the C#
exporter can be written without redoing any of the research.

**Not blocked.** Remaining work is the C# port — see [Next steps](#7-next-steps).

---

## 1. Format decision: `.ibt`, not `.pds`

Pi Toolbox's sim-racing workbook outing import accepts only `.pds`, `.ibt`, and `.mp4`
(observed in the real UI). That rules out **Pi ASCII**, which is otherwise the
Cosworth-sanctioned route for third-party data and would have been nearly free given we
already emit TSV — ASCII import is a full-Toolbox feature the iRacing-flavored build
doesn't expose.

**`.pds` is ruled out** and should not be revisited without new information:

- No public spec. Cosworth gates third-party PDS creation behind a COM component ("PI DO
  Wrapper"); their forum states the output is readable by Cosworth software only, and
  users report it erroring when writing actual sample data.
- **Channel identity is not in the file.** Per brakepoint.io's Pi/PDS guide, the
  name↔data mapping "depends on the CAN configuration set up by the user or the engineer
  who installed the logger", so readers fall back to content-based detection (value
  ranges, correlations). Only a few `ch_id`s are stable (GPS Lat 502, Lon 532, RPM 213);
  ECU speed appears as 199/168/721/112 depending on session. Fatal for *writing* — a
  byte-perfect PDS could still show unlabelled channels.
- The strongest open-source effort, [`tobi/duckdb_motorsport_telemetry`](https://github.com/tobi/duckdb_motorsport_telemetry)
  (Rust; PDS + MoTeC LD + VBOX), **reads PDS but cannot write it** — its only write
  target is MoTeC LD. It credits its specs to "RacingMagick", the upstream to chase if
  PDS is ever revisited.
- No `.pds` sample exists locally, unlike the genuine `.ld`/`.ldx` that made the MoTeC
  port verifiable.

`.ibt` works because Pi Toolbox has first-class iRacing support (Cosworth/iRacing
partnership) and reads `.ibt` natively via **Import → iRacing**.

**Accepted tradeoff:** this labels Forza/GT7/rally stints as iRacing sessions. Pi Toolbox
offers no vendor-neutral door in this build.

---

## 2. `.ibt` binary layout

Already validated byte-by-byte against real captures — see
`../shakedown-engineer/PROJECT_PLAN.md` → "IBT (iRacing) format findings", and the
`binrw` structs in `../shakedown-engineer/crates/sde-formats/ibt/src/raw.rs`, which are a
ready-made write template.

Little-endian. Three regions **back-to-back, no gaps**:

```
header (144 B) -> var headers (144 B each) -> session YAML -> sample buffer
```

### Header @ 0

| Off | Field | Notes |
|----:|---|---|
| 0 | `ver` (i32) | 2 |
| 4 | `status` (i32) | 1 |
| 8 | `tick_rate` (i32) | Hz. **See the 100/60 trap below.** |
| 12 | `session_info_update` (i32) | |
| 16 | `session_info_len` (i32) | YAML byte length |
| 20 | `session_info_offset` (i32) | = `144 + num_vars*144` |
| 24 | `num_vars` (i32) | |
| 28 | `var_header_offset` (i32) | = 144 |
| 32 | `num_buf` (i32) | 1 |
| 36 | `buf_len` (i32) | sample record stride |
| 48..112 | `varBuf[4]` | `{i32 tick_count, i32 buf_offset, i32 pad[2]}`; only slot 0 used, its `buf_offset` at **+52** = `session_info_offset + session_info_len` |

`irsdk_diskSubHeader` @ 112 — `struct.unpack_from('<I4xddii', m, 112)`:

| Off | Field | Notes |
|----:|---|---|
| 112 | `session_start_date` (u32) | unix `time_t`. **Surfaces as the outing's "Create date" in Pi Toolbox** |
| 120 | `session_start_time` (f64) | seconds |
| 128 | `session_end_time` (f64) | seconds |
| 136 | `session_lap_count` (i32) | |
| 140 | `session_record_count` (i32) | |

> Real files can lie: one genuine sample (Hell RX) has a zeroed/unfinalized sub-header
> (`record_count=0`) from an abnormally-ended session.

### Var header (144 B each)

| Off | Field |
|----:|---|
| 0 | `type` (i32) |
| 4 | `offset` (i32) — byte offset **within each sample record** |
| 8 | `count` (i32) — 1 for scalars |
| 12 | `count_as_time` (u8) + 3 B pad |
| 16 | `name` (32 B ASCII, NUL-padded) |
| 48 | `desc` (64 B) |
| 112 | `unit` (32 B) |

Type table: `0`=char(1B), `1`=bool(1B), `2`=int32(4B), `3`=bitfield u32(4B),
`4`=float32(4B), `5`=float64(8B).

Sample buffer: `record_count` records of `buf_len` bytes. Value for record `i` is at
`buf_offset + i*buf_len + var.offset`. Lay out `offset`s respecting natural alignment.

---

## 3. The working recipe (proven live)

Established by **bisection** — swapping the YAML and binary regions between a known-good
and a known-bad file, changing one variable at a time:

- Real 280-var binary + **our** YAML → **loads**. Our YAML is fine.
- **Our 13-var binary** + real YAML → **fails**, and fails hard: *no telemetry parsed at
  all*, not merely missing metadata. The var table was the blocker.
- **26 channels → works.** 112-byte stride, ~2.1 MB for a 5-minute stint (vs ~21 MB for
  the full 280-var table).

### Channel set

The 18 names the converter DLL explicitly references (found by intersecting a real
capture's 280 var names against the DLL's string table):

```
SessionTick, DriverMarker, PlayerTrackSurface, PlayerCarMyIncidentCount,
SteeringWheelAngle, RPM, LapCompleted, LapDist, LapDistPct, Speed, YawNorth,
Lat, Lon, Alt, WindVel, VertAccel, LatAccel, LongAccel
```

Plus the ones we carry data for: `SessionTime, Throttle, Brake, Clutch, Gear, Lap,
IsOnTrack, FuelLevelPct`.

Copy var-header records **verbatim** from a real capture (preserving `name`/`type`/
`unit`/`desc`) and re-pack only the `offset` field for the new stride. Nothing invented.

`Throttle`/`Brake`/`Clutch`/`Gear` are **not** in the referenced list — non-referenced
channels pass through generically off the var-header names.

### Session YAML contract

Extracted from the importer DLL (see §4). Every one of these paths is navigated:

| Section | Keys |
|---|---|
| `WeekendInfo` | `TrackName`, `TrackLength`, `TrackLengthOfficial`, `TrackConfigName`, `TrackDisplayShortName`, `TrackDisplayName`, `SessionID`, `BuildVersion`, `BuildType` |
| `DriverInfo` | `DriverCarIdx`, `DriverCarIsElectric`, `DriverCarEstLapTime`, `Drivers:<n>:{CarID, CarScreenName, UserName, IsSpectator}` |
| `SessionInfo` | `Sessions:0:SessionNum` |
| `SplitTimeInfo` | `Sectors` |
| `CarSetup` | `InCarSystems:GearRatios` — **optional**, real captures load without it |

Match iRacing's dialect: **CRLF** line endings, `---` / `...` document wrappers,
single-space indentation, list items as ` - Key: value`.

Rejection message to watch for: **`This dataset contains invalid session properties`**.

The driver is selected via `DriverInfo:DriverCarIdx` — *not* by matching `DriverUserID`
against `Drivers[].UserID`. (That is `TrackDataAnalysis`'s mechanism; don't confuse the
two.) An earlier conclusion that `SplitTimeInfo`/`CarSetup` are cosmetic came from that
reader's narrow behaviour and is **wrong** for the real importer.

### Unit conversions

| shtep | iRacing var | Conversion |
|---|---|---|
| `Speed_kmh` | `Speed` (m/s) | ÷ 3.6 |
| `Throttle_pct` / `Brake_pct` / `Clutch_pct` | `Throttle`/`Brake`/`Clutch` | **÷ 100** |
| `Gear` | `Gear` (i32) | direct (−1=R, 0=N) |
| `LapDistance_m` | `LapDist` (m) | direct |
| `LapNumber` | `Lap` (i32) | direct |
| `Time_s` | `SessionTime` (f64 s) | direct |

> Channels whose `unit` is exactly `%` are stored as **0..1 ratios**, not 0–100. shtep
> passes 0–100 through as-is (a deliberate, live-confirmed fix), so this is the exact
> inverse of the old `*100` bug.

### Two traps that produce a file passing every structural check while still being wrong

1. **100 Hz vs 60 Hz.** `PluginSettings.SampleRateHz` defaults to **100**; iRacing is
   **60**. Readers reconstruct time as `i * (1000/tick_rate)`, so declaring 60 while
   writing 100 Hz data stretches a 322 s stint to 537 s — **1.667× off** — with every
   value round-tripping correctly. Either declare the true rate or genuinely resample.
   *Test invariant:* `record_count/tick_rate` must agree with `session_end_time`, **and**
   the reconstructed axis must match the stored `SessionTime` channel.
2. **`SessionTime` must come from `Time_s`,** not be computed as `i/tick_rate`. The two
   agree only for a perfectly uniform session; any pause or rewind makes them diverge.
   See the flagged pause-gap issue in `PLUGIN_IMPLEMENTATION_PLAN.md` notes.

---

## 4. Where the importer lives

```
C:\Program Files\Pi Toolbox iRacing Telemetry Converter\
  Toolbox iRacing Telemetry Converter.exe        (GUI only - no CLI args)
  Converters\Pi.Research.Toolbox.TelemetryConverter.iRacing.dll   <- the importer
```

Mixed-mode C++/CLI with **yaml-cpp statically linked**, so it genuinely parses the
session YAML. The YAML contract in §3 was extracted from its string table (colon-
delimited paths like `DriverInfo:Drivers:%ld:CarScreenName`).

It builds lap markers from `LapCompleted`/`LapDist`/`LapDistPct` (strings `LapMarker`,
`LapMarkerType` present) and special-cases `Speed`, `Latitude`/`Longitude`/`Altitude`,
`LatAccel`/`LongAccel`.

**The converter has no CLI**, so the import test cannot be automated — it must go
through the UI. (`*.tbxtel` is a converter *project* file filter, not a batch mode.)

Also present: `C:\Program Files\Pi Data Access Objects\` (`PiDataAccess.dll` + PDBs) —
the PDS-writing SDK, if `.pds` is ever reconsidered.

---

## 5. Channel availability from SimHub

> **Superseded in part.** PR #4 (merged 2026-08-03) expanded the plugin to **133
> channels**, so much of the "not yet mapped" list below is now recorded. What a given
> *sim* actually populates is the real constraint. For GT7 specifically, measured live:
> **live** — `LapNumber`, `Speed_kmh`, `RPM`, `Gear`, pedals, `FuelLevel_pct`,
> `LatAccel_g`/`LongAccel_g`/`VertAccel_g`, `TyreTempFL_C` (62–78 °C), `ABSActive`,
> `TCActive`, `CarPosX/Y/Z`; **dead/empty** — `SteerRatio`, `SuspTravel*`, `WheelLoad*`,
> `TyrePressure*`, `BrakeTemp*`, `AirTemp_C`, `TrackTemp_C`, `PitLimiterOn`,
> `Handbrake_pct`, `LapDistancePct` (flat −1).
>
> `CarPosX/Y/Z` are live world metres (−2045..−911, 88..145, 1636..3167) — the raw
> material for a track map, but converting to `Lat`/`Lon` needs a projection choice that
> hasn't been made.

Reflected from the installed `GameReaderCommon.dll` — `GameReaderCommon.StatusDataBase`
has **257 properties**.

### Mappable but not yet in `ChannelMap.cs` — these are the "No Value" gauges

| iRacing channel | `StatusDataBase` source |
|---|---|
| `LatAccel` / `LongAccel` / `VertAccel` | `AccelerationSway` / `AccelerationSurge` / `AccelerationHeave` |
| `BrakeABSactive` | `ABSActive` (also `ABSLevel`) |
| `AirTemp` | `AirTemperature` |
| `TrackTemp` | `RoadTemperature` |
| `LapDistPct` | `TrackPositionPercent` (real value, better than integrating) |
| `LFpressure` etc. | `TyrePressure{FrontLeft,FrontRight,RearLeft,RearRight}` |
| `LFtempCM` etc. | `TyreTemperature{FrontLeft,…}` |
| — | `BrakeTemperature{FrontLeft,…}`, `OilTemperature`, `OilPressure` also available |

### Permanently unavailable — no such property exists (verified; don't re-investigate)

`SteeringWheelAngle`, `WheelLockLF/LR/RF/RR`, `FrontRideHeight`/`RearRideHeight`,
suspension travel. Those workbook gauges stay empty regardless of file format.

### Worth checking

`CarCoordinates` (`Double[]`) **does** exist. The older note that `PosX/PosY/PosZ` don't
exist is true only of those exact names. If it's populated for a given sim it could feed
`Lat`/`Lon`/`Alt` and produce the track map (currently "No Map"). Verify against a
SimHub replay before relying on it.

---

## 6. Circuit validation — DONE (2026-08-07)

Validated against `GranTurismo7_DT__260804111815_20260804_233116.tsv`: **7 laps, 514 s,
100 Hz, 51,445 rows, zero non-monotonic and zero bad-width rows.** Lap times 80.2 /
80.2 / 79.2 / 81.2 / 83.3 s. The generated `.ibt` loads with the **lap selector working
as expected** — lap markers and per-lap comparison both function.

Generated file: 27 channels, 112-byte stride, 3.46 MB for a 514 s / 7-lap session.

### Circuit-specific findings

- **`LapDistance_m` is unusable for GT7** — and not merely flat-zero like the rally
  sessions. It holds near-constant negative values with rare jumps (lap 3 had *one*
  increasing step across 8,016 rows). **`LapDistancePct` is a flat `-1` sentinel.**
  Both are GT7 sim limitations, not plugin bugs.
- **So `LapDist` is integrated from speed and RESET at each `LapNumber` change** — the
  correct semantic for a circuit anyway. Independent validation: integrated lap
  distances agreed to **0.34%** across 5 full laps (3249.7–3260.9 m), giving a track
  length of 3254.5 m.
- **`LapDistPct` must be clamped to < 1.0.** Derived as `LapDist / track_len`, the out
  lap (3511 m) exceeds the measured track length and produced 1.077 before clamping.
  iRacing's is a 0–1 ratio.
- `Lap` ← `LapNumber`; `LapCompleted` ← `LapNumber - 1`; `session_lap_count` ← max lap;
  `DriverCarEstLapTime` ← median full-lap time.

### The accelerometer unit bug — read this before "fixing" it

`LatAccel_g` spans −21 to +23 in this session. As g that is physically impossible; as
**m/s²** it is ±2.3 g, exactly right for a race car. The values are **m/s² carrying a
misleading `_g` suffix** — the known unfixed naming bug.

This is convenient: iRacing's `LatAccel`/`LongAccel`/`VertAccel` are also m/s², so they
map **straight through with no conversion**. If the naming bug is ever corrected in the
plugin, **do not add a ×9.81 conversion** — the numbers are already right, only the
label is wrong.

### Rewind-fix timing (checked, no impact)

These sessions were captured at 23:21–23:31 on 2026-08-04 with the DLL deployed at
23:17; the GT7 rewind-truncation fix was committed at 23:50 — *after*. Verified the data
is unaffected anyway: `dt` is uniformly 0.01 across all 51,444 intervals, zero duplicate
or backward `Time_s`, and all six lap boundaries have `dt = 0.01`. The four
`Discontinuity` flags sit in the final 63 rows (session end), 448+ rows from any lap
boundary. Redeploy `main` for future captures, but this data needed no re-recording.

## 6b. Rally path (single stage, no laps)

The earlier rally sessions have no `LapNumber` column at all (it wasn't in the default
`EnabledChannels` until 2026-08-01) and a flat-zero `LapDistance_m`. That path writes
`Lap` as a constant and integrates `LapDist` cumulatively **without** resetting —
correct for a stage, where total stage distance is the meaningful axis. It produces no
lap markers, which is expected and appropriate there.

Both paths are validated; the exporter needs to pick between them based on whether
`LapNumber` varies.

### Test data notes

Best rally session, if a comparison is ever needed:
`E:\telemetry\sessions\AssettoCorsaRally_Greece_Zeli_20260731_233029.tsv` — 32,235 rows,
322.34 s, exactly 100 Hz (single `dt`=0.01), zero pauses/discontinuities/non-monotonic
rows, speed 0–167.9 km/h, integrated distance 3,817.8 m.

Do **not** use `RBR_Passauna_II_20260727_234636.tsv` — the car never moved (max speed
0.01 km/h); its non-zero `LapDistance_m` is a stuck constant.

**Every recorded `.tsv` has its channel columns repeated N times** (RBR 6×, ACR 4×) from
the `ReadCommonSettings` list-append bug. The data is fine — **parse by first
occurrence**. Note `csv.DictReader` silently keeps the *last*.

---

## 7. Implementation

Ported, 2026-08-07:

- `Export/IbtWriter.cs` — binary layout, var headers, session YAML.
- `Export/IbtExporter.cs` — reads the finished `.tsv`/`.meta.json` pair, derives laps and
  distance, resamples, assembles channels. Also holds `IbtChannelSet`, the 27 embedded
  var definitions (copied verbatim from a genuine capture, so no sample file is needed
  at runtime).
- Settings: `ExportIbt` (off by default), `IbtOutputDir`, `IbtTickRateHz` (60).
- `Plugin.cs` calls it after `Close()`, in a try/catch, exactly like the MoTeC export —
  the hot recording path is untouched.
- 27 tests in `IbtExporterTests.cs`, covering layout, the required-channel set, offset
  alignment/overlap, **both time-axis traps**, unit conversions, circuit vs stage lap
  handling, the YAML key contract, duplicated columns, and degenerate input.

**Validated against the real thing:** run over the 7-lap GT7 session, the C# output is
identical to the Python prototype Pi Toolbox accepted — same record count (30,867),
stride (112), lap count, session date, and **all 27 channels agreeing to 0.000000**.
Only the YAML differs (20 bytes: `BuildVersion`).

One bug the tests caught that structural checks would not: interpolating `LapDist`
across a lap reset ramps the value *down* through the final fraction of every lap (a
99 m → 0 step reads as 82 m mid-way). `Interpolate(..., holdAcrossDrops: true)` holds
through the discontinuity instead.

### Possible follow-ups (none blocking)

- `WeekendInfo:BuildVersion` is written as `shtep`, which Pi Toolbox displays as
  "System details: iRacing vshtep". Cosmetic; change if it bothers you.
- Map the channels §5 lists as available-but-unmapped (tyre temps are live for GT7).
- Project `CarPosX/Y/Z` into `Lat`/`Lon`/`Alt` for a track map — needs a projection
  choice.
- `SessionTime` is written as `index / tick_rate` on a resampled uniform grid, so a
  session containing a pause is interpolated across rather than preserving the gap.

### Validation resources

- Genuine `.ibt` samples: `../shakedown-engineer/.sample-data/iRacing/`
  (use the Mt Washington one; the Hell RX file has a zeroed sub-header)
- Independent parsers to cross-check output against: the Rust `sde-ibt` crate
  (`../shakedown-engineer/crates/sde-formats/ibt`) and
  `../TrackDataAnalysis/data/iracing.py`
- Prototype scripts and generated test files: `E:\telemetry\ibt-test\`
  (Python; graft/slim/bisect writers plus the verified `.ibt` outputs)
