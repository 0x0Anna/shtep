# shtep

**S**im**H**ub **T**elemetry **E**xport **P**lugin.

A SimHub plugin that records telemetry from rally and circuit sims (RBR,
Dirt Rally, ACR, EA WRC, ACC, AC, iRacing, etc.) to a plain-text TSV + JSON
sidecar file pair, per stage or per stint.

It owns recording only — no MoTeC/.ld knowledge lives here. The output
format is a tool-agnostic intermediate designed to be consumed by an
external converter (see the companion
[shakedown-engineer](https://github.com/0x0Anna/shakedown-engineer)
repo), or opened directly with any TSV-capable tool (Excel, pandas, awk).

## Layout

```
src/
  TelemetryExportPlugin/       # the plugin itself
  TelemetryExportPlugin.Tests/
fixtures/                      # sample TSV/sidecar pairs for testing
SCHEMA.md                      # the TSV + sidecar file format spec
PLUGIN_IMPLEMENTATION_PLAN.md  # v1 design notes
```

## Building

Requires the SimHub Plugin SDK. By default the project looks for SimHub at
`C:\Program Files (x86)\SimHub\`; override with `-p:SimHubInstallPath=...`
or the `SIMHUB_INSTALL_PATH` environment variable if installed elsewhere.

```
dotnet build
```

To copy the built plugin into a running SimHub install (SimHub must be
closed first):

```
dotnet build -t:Deploy
```

See `SCHEMA.md` for the on-disk file format and `PLUGIN_IMPLEMENTATION_PLAN.md`
for the design rationale.

## Third-party dependencies

Built against the [SimHub](https://www.simhubdash.com/) Plugin SDK
(`GameReaderCommon`, `InputManagerCS`, `log4net`, `MahApps.Metro`,
`Newtonsoft.Json`, `SimHub.Logging`, `SimHub.Plugins`), referenced from a
local SimHub install and not redistributed by this repo.

The test project additionally pulls [xUnit.net](https://xunit.net/) and
[Newtonsoft.Json](https://www.newtonsoft.com/json) from NuGet as dev-time
dependencies.

## License

MIT — see [LICENSE](LICENSE).
