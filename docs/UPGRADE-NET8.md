# Aggiornamento a .NET 8 e stack UI moderno

Branch: `upgrade/net8-modern-ui` (segue `fix/persistent-performance-reporting`)

## Target framework

| Progetto | Prima | Dopo |
|----------|-------|------|
| `TwinCatAdsTool.Interfaces` | `net5.0` | `net8.0` |
| `TwinCatAdsTool.Logic` | `net5.0` | `net8.0` |
| `TwinCatAdsTool.Gui` | `net5.0-windows10.0.19041` | `net8.0-windows10.0.19041` |
| `TwinCatAdsTool` | `net5.0-windows10.0.19041` | `net8.0-windows10.0.19041` |
| `Tests/TwinCatAdsTool.Logic.Tests` | (fuori solution) | `net8.0`, **nella solution** |

`RuntimeIdentifier` passa da `win10-x64` (deprecato in .NET 8) a `win-x64`.

Il TFM della GUI mantiene il suffisso `10.0.19041`: **non è arbitrario**, `System.Reactive`
espone `ObserveOnDispatcher` per WPF solo sotto quel target. Rimuoverlo rompe la build.

`Directory.Build.props` attiva `EnableWindowsTargeting` sui soli host non-Windows: da .NET 6
in poi il targeting pack Windows arriva via NuGet, quindi **i progetti WPF si compilano anche
da macOS e Linux**. L'esecuzione resta ovviamente Windows.

## Pacchetti

| Pacchetto | Prima | Dopo | Note |
|-----------|-------|------|------|
| ReactiveUI / ReactiveUI.WPF | 9.11.1 | 24.1.0 | breaking, vedi sotto |
| System.Reactive | 5.0.0 | 6.0.1 | |
| DynamicData | (transitivo) | 9.4.33 | ReactiveUI l'ha rimosso dalle transitive in v19 |
| MahApps.Metro | 2.2.0 | 2.4.11 | |
| MaterialDesignThemes.MahApps | 0.1.4 | 5.3.2 | breaking sul theming |
| MaterialDesignExtensions | 3.2.0 | **rimosso** | fermo al 2021, nessun controllo usato |
| Extended.Wpf.Toolkit | 3.6.0 | 5.1.2 | |
| OxyPlot.Wpf | 1.0.0 | 2.2.0 | breaking sulle legende |
| FontAwesome.WPF | 4.7.0.9 | **rimosso** | fermo al 2017, solo .NET Framework |
| DiffPlex | 1.4.4 | 1.9.0 | |
| Humanizer | 2.8.26 | 3.0.10 | |
| Ninject | 3.3.4 | 3.3.6 | |
| JetBrains.Annotations | 2020.3.0 | 2026.2.0 | |
| Beckhoff.TwinCAT.Ads(.Reactive) | 5.0.327 | 5.0.379 | resta sulla linea 5.x di proposito |
| TwinCAT.JsonExtension | 1.1.0-beta67 | **rimosso** | non più usato dopo la riscrittura |

Zero warning `NU1701`: nessun pacchetto .NET Framework-only è più in gioco.

### Perché ADS resta sulla 5.x

La 6.x è un aggiornamento con breaking change proprio sul livello che il branch precedente ha
riscritto. Cambiare libreria ADS e motore di backup nello stesso passo renderebbe impossibile
attribuire un eventuale malfunzionamento. Va affrontato come passo separato, dopo la validazione
sul campo.

## Modifiche al codice richieste dagli aggiornamenti

**ReactiveUI 24 — `Unit` → `RxVoid`.** Le firme dei comandi non usano più
`System.Reactive.Unit` ma `ReactiveUI.Primitives.RxVoid`. I sei ViewModel sono stati adeguati.
`RxVoid` entra tramite **alias di tipo**:

```csharp
using RxVoid = ReactiveUI.Primitives.RxVoid;
```

e non con un `using` di namespace: `ReactiveUI.Primitives` contiene anche `LinqExtensions` e
`SubscribeExtensions`, i cui `Do`/`Select`/`Where`/`Subscribe` sono ambigui rispetto a
`System.Reactive.Linq`.

**OxyPlot 2 — legende.** `PlotModel.LegendPosition` e affini non esistono più: le impostazioni
vivono in `PlotModel.Legends`, come oggetti `Legend` (namespace `OxyPlot.Legends`).

**FontAwesome → PackIcon.** L'unica icona usata (`fa:ImageAwesome`) è ora
`materialDesign:PackIcon`; `ConnectionStateToIconConverter` restituisce un `PackIconKind`
invece di una stringa. Il namespace `xmlns:fa` è stato tolto dai sei file che lo dichiaravano
senza usarlo.

**MaterialDesign 5 — theming.** I dictionary che `App.xaml` univa
(`MaterialDesignTheme.Light.xaml`, `Generic.xaml`, `MaterialDesignTheme.Defaults.xaml`)
**non esistono più**. I default stanno in `MaterialDesign2.Defaults.xaml`, e `BundledTheme`
costruisce la palette al posto delle dictionary di colore unite a mano e delle
`SolidColorBrush` derivate. I dictionary MahApps sono rimasti invariati.

## CI

`.github/workflows/dotnet-core.yml`: `setup-dotnet@v4` con .NET 8, `checkout@v4`, e la serie
di tag passa da `1.2.x` a `1.3.x` (allineata a `Constants.Version`).

**`dotnet test` ora esegue davvero dei test**: il progetto è nella solution, quindi i 48 test
del motore di backup/restore girano a ogni build. Prima quello step non testava nulla.

## Stato della verifica

**Verificato**:
- solution completa in Debug e Release: **0 errori**, e nessun `NU1701`;
- 48 test verdi in Debug e in Release;
- `dotnet publish` per `win-x64` produce `TwinCatAdsTool.exe` single-file (~52 MB) — la
  pipeline di rilascio regge.

**Non verificabile senza eseguire l'app** — i pack URI dei `ResourceDictionary` si risolvono a
runtime, non in compilazione:
1. che la finestra si apra con il tema corretto;
2. l'aspetto dei controlli MahApps + MaterialDesign 5 affiancati;
3. l'icona di stato connessione (`PackIcon`);
4. la legenda del grafico con OxyPlot 2.

I cinque stili MaterialDesign usati dalle view (`MaterialDesignRaisedButton`,
`MaterialDesignFloatingActionButton`, `MaterialDesignFloatingActionMiniButton`,
`MaterialDesignIconForegroundButton`, `MaterialDesignTabControl`) sono stati verificati come
presenti nel pacchetto 5.3.2 ispezionandone l'assembly.

**Se la finestra non si apre**: il theming è isolato in un commit unico.

```
git revert 23c87b6      # Move to material design 5 and drop MaterialDesignExtensions
```

riporta al tema precedente lasciando in piedi tutto il resto dell'aggiornamento.
