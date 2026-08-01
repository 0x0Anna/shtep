# SimHub Telemetry Export Plugin — v1 Implementation Plan

Companion repo to `shakedown-engineer`. Writes TSV + JSON sidecar pairs per
`SCHEMA.md` (v1.1). Recording itself owns no MoTeC/.ld knowledge — that
logic is isolated in `Export/` (`MotecLdWriter.cs`, `MotecExporter.cs`) as
an optional post-processing step, run only after a recording's TSV/sidecar
pair has been closed and moved to `OutputDir`, gated behind the
`ExportMotecLd` setting (default off). This lets shtep produce `.ld` files
standalone, without the Rust converter, while keeping the hot recording
path (`RecordingSession`, `SampleTimer`, `RewindIndex`, boundary detection)
entirely unaware `.ld` export exists.

## Project setup

- SimHub PluginSDK project (`User.PluginSdkDemo` template as starting point,
  per SimHub's own plugin/extension SDK docs).
- Target the same .NET version SimHub's SDK expects — confirm against the
  currently installed SimHub PluginSdk demo project rather than assuming.
- Repo layout suggestion:
  ```
  src/
    TelemetryExportPlugin/       # the plugin itself
      Plugin.cs                  # SimHub entry point, lifecycle hooks
      Recording/
        RecordingSession.cs      # open/write/close/move for one file pair
        SampleTimer.cs           # fixed-rate tick, holds last-known values
        ChannelMap.cs            # StatusDataBase property -> header name
        CrashRecovery.cs         # startup scan/truncate/purge of .partial
        DiscontinuityDetector.cs # sim-event + heuristic detection, both
                                 # forward (reset/assist) and backward (rewind)
        RewindIndex.cs           # lightweight (position, byteOffset) index
                                 # + truncate-and-resume logic
      Config/
        PluginSettings.cs        # TempDir, OutputDir, sample rate, purge flag
        PathValidation.cs        # create-if-missing, warn-if-can't
      Boundaries/
        RallyBoundary.cs         # stage-start/stage-end event subscription
        CircuitBoundary.cs       # pit-lane state + debounce -> stint start/end
      Export/
        MotecLdWriter.cs         # binary .ld writer (ported from MotecLogGenerator)
        MotecExporter.cs         # TSV+sidecar -> .ld post-processor, opt-in
    TelemetryExportPlugin.Tests/
  ```

## Config surface (plugin settings UI)

- `TempDir` (path, required)
- `OutputDir` (path, required)
- `SampleRateHz` (int, default 100)
- `PurgeIncompleteOnStartup` (bool, default false)
- `PitLaneDebounceMs` (int, default ~1000-2000) — circuit only, avoids a
  momentary pit-lane flicker near the line spawning a bogus stint file
- `DiscontinuityDetection` (`SimEvent` | `Heuristic` | `Both`, default `Both`)
- `HeuristicDiscontinuitySpeedKmh` (int, default ~400) — implausible-speed
  threshold used for the heuristic fallback
- `RewindHandling` (`Truncate` | `FlagOnly`, default `Truncate`)
- Channel selection (which canonical channels to log) — can start as a fixed
  list for v1 and become a proper checklist UI later; don't over-build this
  before you know which `StatusDataBase` properties are actually reliable
  per-sim
- `ExportMotecLd` (bool, default `false`) — after each recording closes,
  also write a MoTeC `.ld` file from the finished TSV/sidecar pair
- `MotecOutputDir` (path, optional) — destination for `.ld` files; blank
  means same as `OutputDir`

All path fields validated at save time per SCHEMA.md's "Startup & config
validation" section — attempt create, warn clearly if it fails.

## Core behaviors, in build order

1. **Fixed-rate sample timer + channel map.** Get a single `.tsv` writing
   correctly for one hardcoded sim first (whichever you have installed to
   test with) before wiring any boundary-detection logic. Validate: correct
   header, invariant-culture numbers, UTF-8 no BOM, `\n` endings, held values
   between ticks.
2. **Write-to-temp, move-on-close.** `.tsv.partial` in TempDir → sidecar
   written → sidecar moved first → `.tsv` renamed/moved last, per SCHEMA.md.
   Test the ordering explicitly (a test that kills the process between steps
   and confirms the converter-facing contract still holds).
3. **Rally boundary (stage-start/stage-end events).** Wire to whichever
   SimHub sim adapter exposes these directly — should be the simpler of the
   two boundary types since it's event-driven, not inferred.
4. **Pause handling.** `Paused` column, stop-cadence-during-pause, single
   transition rows on enter/exit.
5. **Discontinuity handling.** `Discontinuity` column; check each sim you
   support for an exposed reset/assist-wait state first (`SimEvent` tier),
   then implement the position/distance-delta heuristic as the required
   fallback (`Heuristic` tier) — needed even where a sim event exists, since
   it's your only signal at all for sims that don't expose one (Forza's
   fast-travel, most likely). Populate the sidecar's `discontinuities` array
   from whichever tier fired. Test against a fixture with a deliberate
   position jump to confirm the heuristic threshold behaves sensibly before
   relying on it live.
6. **Rewind handling.** Build directly on top of the discontinuity heuristic
   — a rewind is a *backward* jump in position/distance, as opposed to the
   forward jumps that resets/assists produce; direction is the distinguishing
   signal. Maintain the lightweight in-memory `(position or LapDistance,
   byteOffset)` index while writing (keyed on position where the sim exposes
   it, `LapDistance` as fallback — note this can misfire across a lap
   boundary). On detection: flush, reopen read/write, seek to the matched
   offset, `SetLength()` to truncate, trim the index, resume writing. `Time_s`
   needs no adjustment since it's a synthetic row counter, not wall-clock.
   Populate the sidecar's `rewinds` array. Test against a fixture that
   simulates: write N rows, inject a backward jump, confirm the file is
   correctly truncated and appending resumes cleanly from the truncation
   point (not corrupted, not double-writing headers, etc).
7. **Crash recovery on startup.** Scan TempDir for orphaned `.partial` files:
   truncate-to-last-valid-row + sidecar with `recoveredFromCrash: true` by
   default, or purge if `PurgeIncompleteOnStartup` is set. Test against a
   deliberately-truncated fixture file (cut off mid-row) to confirm the
   line-completeness check works.
8. **Circuit boundary (pit-lane state + debounce).** Build this after rally
   is solid — it's the trickier one since it's inferred from a boolean
   property rather than a discrete event.
9. **Multi-sim channel maps.** Extend `ChannelMap` per sim as you test against
   each one — expect some `StatusDataBase` properties to be present in some
   sims and absent in others; missing channel = omitted column, per spec.

## Testing notes

- Unit-testable without SimHub running: `RecordingSession` (file lifecycle),
  `CrashRecovery` (fixture-based), `PathValidation`, `SampleTimer`'s
  hold-last-value logic, `RewindIndex` (fixture-based: write N rows, inject a
  backward jump, assert correct truncation and clean resume) — none of these
  need a live sim.
- Needs live SimHub + a sim to test: boundary detection (both kinds), actual
  `StatusDataBase` property availability per sim, real-world discontinuity/
  rewind detection thresholds (RBR reset/assist, Forza fast-travel and
  rewind).
- Worth hand-authoring fixture `.tsv`/`.meta.json` pairs early (valid,
  truncated-mid-row, paused-transition, discontinuity, and rewind examples)
  — useful both for this repo's own tests and as the first fixtures the Rust
  converter can build against, before either side has the other's real
  output to test with.

## Explicit non-goals for v1

- No .ld/.ldx knowledge in the live recording path (`RecordingSession`,
  `SampleTimer`, boundary detection, etc.) — that stays isolated to the
  opt-in `Export/` post-processor, which runs after a recording is already
  closed and moved to `OutputDir`. `shakedown-engineer` remains a valid,
  independent way to produce `.ld` files from the TSV/sidecar pair; this
  repo's own exporter is an alternative for standalone use, not a
  replacement for that companion repo.
- No lap/marker offsets embedded in circuit files (per-stint files instead).
- No binary framing, no external DB/broker in the recording path.
- No live fan-out to other consumers — out of scope until an actual need for
  it shows up.
- No attempt to distinguish rewind sub-cases (e.g. how far back, whether it
  crosses a lap boundary) beyond what `RewindIndex`'s position/LapDistance
  fallback already gives you — revisit only if the fallback proves unreliable
  in practice.
