# .NET 8 and the package upgrade

## Target framework

| Project | Before | After |
|---|---|---|
| `TwinCatAdsTool.Interfaces` | `net5.0` | `net8.0` |
| `TwinCatAdsTool.Logic` | `net5.0` | `net8.0` |
| `TwinCatAdsTool.Gui` | `net5.0-windows10.0.19041` | `net8.0-windows10.0.19041` |
| `TwinCatAdsTool` | `net5.0-windows10.0.19041` | `net8.0-windows10.0.19041` |
| `Tests/TwinCatAdsTool.Logic.Tests` | (outside the solution) | `net8.0`, **in the solution** |

`RuntimeIdentifier` moves from `win10-x64`, deprecated in .NET 8, to `win-x64`.

The GUI keeps the `10.0.19041` suffix on its TFM. This is **not arbitrary**: `System.Reactive`
exposes `ObserveOnDispatcher` for WPF only under that target, and removing it breaks the build.

`Directory.Build.props` enables `EnableWindowsTargeting` on non-Windows hosts only. From .NET 6 on,
the Windows targeting pack comes through NuGet, so **the WPF projects build from macOS and Linux**.
Running them, of course, still needs Windows.

## Packages

| Package | Before | After | Notes |
|---|---|---|---|
| ReactiveUI / ReactiveUI.WPF | 9.11.1 | 24.1.0 | breaking, see below |
| System.Reactive | 5.0.0 | 6.1.0 | 6.1 required by ADS 7 |
| DynamicData | (transitive) | 9.4.33 | ReactiveUI dropped it from its transitives in v19 |
| Extended.Wpf.Toolkit | 3.6.0 | 5.1.2 | |
| OxyPlot.Wpf | 1.0.0 | 2.2.0 | breaking on legends |
| DiffPlex | 1.4.4 | 1.9.0 | |
| Humanizer | 2.8.26 | 3.0.10 | |
| Ninject | 3.3.4 | 3.3.6 | |
| JetBrains.Annotations | 2020.3.0 | 2026.2.0 | |
| Beckhoff.TwinCAT.Ads(.Reactive) | 5.0.327 | 7.0.317 | see below |
| TwinCAT.JsonExtension | 1.1.0-beta67 | **removed** | unused after the engine rewrite |
| MahApps.Metro | 2.2.0 | **removed** | replaced by WPF-UI, see `UI-FLUENT.md` |
| MaterialDesignThemes.MahApps | 0.1.4 | **removed** | same |
| MaterialDesignExtensions | 3.2.0 | **removed** | last released 2021, no control used |
| FontAwesome.WPF | 4.7.0.9 | **removed** | last released 2017, .NET Framework only |
| WPF-UI | — | 4.3.0 | |

No `NU1701` warnings: no .NET Framework-only package is in play any more.

## ADS 7: one binary for TwinCAT 4024 and 4026

The initial plan was not to change the ADS library and the backup engine in the same step, so that a
malfunction could be attributed to one or the other.

**The upgrade then became compulsory.** On a PC with **TwinCAT 4026** the tool could no longer
connect, failing with `AdsException: Cannot register Port '0'` against any target, including the PC
itself. The cause is the local channel to the router: 5.x reaches it **only over TCP loopback** on
`127.0.0.1:48898`, while 4026 changed that layer (it can use a unix socket,
`C:\ProgramData\Beckhoff\TwinCAT\3.1\Ams\tcsyssrv.ams.sock`). The same executable worked on a 4024
machine — the proof that the code was not at fault.

| Version | TwinCAT supported |
|---|---|
| 5.x | up to 4024, no support for the 4026 router |
| 6.x | **requires** >= 4024.10 |
| 7.x | **2.11 and later** — covers 4024 and 4026 with one binary |

7.x is therefore the only version that satisfies the requirement of running against both, and is
what is used (`Beckhoff.TwinCAT.Ads` and `.Reactive` 7.0.317, with `System.Reactive` moved to 6.1.0
as 7.x requires).

### What it cost in code

Three compilation points:

- `DT.Date` no longer exists: use `DT.Value` (`ValueCoercion.Normalize`, `DtToDateTimeConverter`);
- `DATE.Date` still exists but changes type, aligned to `DATE.Value`;
- `TcVersion.Build` became `short`, so `DeviceFinder` needs an explicit cast.

### The change the compiler does not report

The PlcOpen types now expose `DateTime` instead of `DateTimeOffset`. **The offset was an artefact of
the wrapper** — a PLC `DT` has no time zone — so a backup now records the timestamp as the PLC holds
it, without the local offset 5.x used to attach.

The round trip is unchanged, verified by running the library: `DT` → `Normalize` → JSON →
`TryCoerce` → `DT` gives back the **same ticks**, that is the same value written to the PLC. The
`DT(DateTimeOffset)` and `TIME(TimeSpan)` constructors still exist, so **backups produced with 5.x
remain restorable**.

One test was deliberately updated: it asserted that `Normalize` of a `DT` returned a
`DateTimeOffset`, which is exactly the type that changed. The ones that check the instant and the
round trip were left as they were, and pass.

## Code changes the upgrades required

**ReactiveUI 24 — `Unit` → `RxVoid`.** Command signatures no longer use `System.Reactive.Unit` but
`ReactiveUI.Primitives.RxVoid`. `RxVoid` is brought in through a **type alias**:

```csharp
using RxVoid = ReactiveUI.Primitives.RxVoid;
```

and not with a namespace `using`: `ReactiveUI.Primitives` also contains `LinqExtensions` and
`SubscribeExtensions`, whose `Do` / `Select` / `Where` / `Subscribe` are ambiguous against
`System.Reactive.Linq`.

**ReactiveUI 24 no longer registers itself on first use.** Without an explicit
`RxAppBuilder.CreateReactiveUIBuilder().WithPlatformServices().Build()`, the first `WhenAnyValue`
throws a `TypeInitializationException` and the window never opens. `ReactiveUI.Wpf` has to be loaded
explicitly first: the WPF registrations, the dispatcher scheduler among them, live in that assembly,
and nothing in this application references a type from it, so the runtime would never load it and
the builder would find no platform to register.

**OxyPlot 2 — legends.** `PlotModel.LegendPosition` and friends are gone; the settings live in
`PlotModel.Legends` as `Legend` objects (`OxyPlot.Legends`).

## Startup diagnostics

`FreeConsole` runs before anything else and the logger may not be up yet, so an exception during
startup left no trace at all: the window simply never appeared. The `catch` made it worse by calling
the logger, which is one of the things that can fail, losing the original exception.

Startup failures are now written to `startup-error.txt` and shown in a message box, with the OS and
runtime version alongside. Each step that can fail on its own no longer takes the application down
with it. See the [README](../README.md) for the logging defects fixed at the same time.

## CI

`.github/workflows/dotnet-core.yml`: `setup-dotnet@v4` with .NET 8, `checkout@v4`, and the tag
series moves from `1.2.x` to `1.3.x`, aligned to `Constants.Version`.

**`dotnet test` now runs actual tests.** The test project is in the solution, so the engine's tests
run on every build. Before, that step tested nothing: there was no .NET test project at all.
