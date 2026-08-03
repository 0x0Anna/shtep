# SimHub Plugin SDK reference assemblies

These DLLs come from a local [SimHub](https://www.simhubdash.com/) install
(`C:\Program Files (x86)\SimHub\`) and are committed here **solely so CI can
compile `TelemetryExportPlugin.csproj` without a full SimHub install**.

`ACSharedMemory.dll` is included because `RawPhysicsAccessor.cs` casts
`StatusDataBase.GetRawDataObject()` to `ACSharedMemory.ACR.Reader.ACRRawData`
for the AssettoCorsaRally raw-physics channels.

They are compile-time references only (`<Private>False</Private>` in the
csproj) — never copied into this plugin's build output and never
redistributed as part of a release of this repo. At runtime, the plugin
always loads SimHub's own copies of these DLLs from wherever SimHub is
actually installed on the end user's machine.

Ownership/license of each DLL belongs to its respective author (SimHub,
Apache log4net, Newtonsoft.Json, MahApps.Metro) — this folder does not grant
any additional rights to them beyond referencing them at build time.

(`InputManagerCS.dll` was deliberately left out here — the plugin project
referenced it but never actually used it; the reference was removed.)

Update these if `TelemetryExportPlugin.csproj`'s reference list changes, or
if a local SimHub update introduces a breaking API change against
`ChannelMap.cs`'s reflected assumptions.
