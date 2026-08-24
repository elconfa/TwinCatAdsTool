# Analisi — TwinCatAdsTool

Repository: https://github.com/fbarresi/TwinCatAdsTool
Autore: Federico Barresi — Licenza MIT (2019) — Sponsor: evopro AG
Commit analizzato: `03ab6c7` (2026-04-17, master) — clonato con `--depth=1`
Data analisi: 2026-08-24

> **Stato:** questo documento fotografa il repository **come clonato da upstream**.
> Il branch `fix/persistent-performance-reporting` ha gia corretto i punti 4, 7 e 8 del
> capitolo 6 e riscritto il motore di backup/restore. Vedi `docs/FIX-PERSISTENT.md`.

---

## 1. Cos'è

Applicazione desktop **WPF (Windows)** che funge da **alternativa leggera a TwinCAT XAE / Visual Studio**
per interagire via **ADS** con un PLC Beckhoff TwinCAT 3.

Funzionalità principali (una tab per funzione):

| Tab | Scopo |
|-----|-------|
| **Backup** | Legge tutte le variabili `PERSISTENT` del PLC e le serializza in JSON, salvabile su file |
| **Restore** | Ricarica un JSON di backup e riscrive i valori sul PLC (con conferma) |
| **Compare** | Diff side-by-side (DiffPlex) fra due backup, o fra backup e valori live |
| **Explore** | Albero dei simboli + ricerca full-text, osservazione live dei valori, scrittura valori, grafico temporale (OxyPlot) |

Requisiti runtime: **TwinCAT ADS installato** sulla macchina e una **route ADS** configurata verso il PLC.

## 2. Struttura della solution

`TwinCatAdsTool.sln` — 4 progetti:

```
TwinCatAdsTool/              → eseguibile WinExe (net5.0-windows10.0.19041, win10-x64, PublishSingleFile)
  Program.cs                   entry point: FreeConsole + Ninject StandardKernel + log4net + avvio MainWindow
  log.config                   config log4net (2 repository: applicativo + "observation")

TwinCatAdsTool.Gui/          → WPF: View (XAML), ViewModel, Converter, ViewModelLocator
TwinCatAdsTool.Logic/        → servizi concreti (ClientService, PersistentVariableService, DeviceFinder)
TwinCatAdsTool.Interfaces/   → contratti + modelli + LoggerFactory (nessuna dipendenza dai due precedenti)

Tests/PlcProject/            → progetto TwinCAT dummy (MAIN, DUT, GVL, FB annidate, variabili PERSISTENT)
                               NON è un progetto di test .NET: serve come PLC di prova manuale
```

Dipendenza: `Gui` e `Logic` → `Interfaces`. L'eseguibile referenzia tutti e tre.

## 3. Stack tecnologico

- **.NET 5.0** (`net5.0-windows10.0.19041`) — fuori supporto Microsoft da maggio 2022
- **Beckhoff.TwinCAT.Ads.Reactive** 5.0.327 + **TwinCAT.JsonExtension** 1.1.0-beta67 (dello stesso autore)
- **ReactiveUI** 9.11.1 + **System.Reactive** 5.0.0 + DynamicData — l'app è interamente Rx-driven
- **Ninject** 3.3.4 per la DI (moduli `GuiModuleCatalog` / `LogicModuleCatalog`)
- **MahApps.Metro**, **MaterialDesignThemes**, **Extended.Wpf.Toolkit**, **FontAwesome.WPF** per la UI
- **OxyPlot.Wpf** per i grafici, **DiffPlex** per il confronto, **Newtonsoft.Json**, **log4net** 3.3.0, **Humanizer**
- Localizzazione via `.resx` (inglese + tedesco) in `Gui` e `Logic`

## 4. Architettura — punti chiave

**MVVM con ReactiveUI.** `ViewModelBase` estende `ReactiveObject`, implementa `IDisposable` e
`IInitializable`: ogni VM ha un `CompositeDisposable Disposables` e un metodo `Init()` chiamato
automaticamente dal `ViewModelLocator` subito dopo la risoluzione da Ninject. Gerarchia:

```
MainWindowViewModel
 ├─ ConnectionCabViewModel      (AmsNetId, porta 851, Connect/Disconnect, stato ADS)
 └─ TabsViewModel
     ├─ BackupViewModel
     ├─ CompareViewModel
     ├─ ExploreViewModel  ─┬─ ObserverViewModel → SymbolObservationViewModel<T>
     │                     └─ GraphViewModel
     └─ RestoreViewModel
```

**`ClientService` (singleton)** è il cuore della comunicazione:
- incapsula un `AdsClient`; espone `ConnectionState` e `AdsState` come `IObservable` (BehaviorSubject);
- **watchdog**: `Observable.Interval(1s)` → `CheckConnectionHealth()` che riconnette automaticamente se
  la connessione cade e pubblica lo stato ADS (errori tradotti in linguaggio umano con Humanizer);
- alla connessione carica **due** collezioni di simboli: `VirtualTree` (per il TreeView) e `Flat` (per la ricerca);
- **auto-discovery**: `DeviceFinder.BroadcastSearchAsync` implementa a mano il protocollo UDP di
  broadcast-discovery ADS (porta UDP di default), popolando la lista dei PLC trovati in rete.
  Codice derivato da `nikvoronin/AdsRemote` (`Logic/Router/*`: Request, Response, Segment, RemotePlcInfo).

**`PersistentVariableService`** — la logica più delicata: itera i simboli con
`s.IsPersistent && InstancePath.Split('.').Length >= 2 && !contiene "["`, tiene solo i simboli la cui
radice non è già nell'iteratore (evita duplicati padre/figlio), legge ciascuno con
`client.ReadJson(path, force:true)` e **ricostruisce l'albero JSON annidato** dai path puntati
(`GVL.Sub.Var` → oggetti annidati). Espone `CurrentTask` come observable per la progress in UI.
Nota: gli array (`[`) sono esclusi dal backup dei persistenti.

**`SymbolObservationViewModel<T>`** — un VM generico per tipo IEC. `ObserverViewModel` fa il dispatch
su `symbol.TypeName` (BOOL, BYTE, WORD, DWORD, SINT/USINT, INT/UINT, DINT/UDINT, REAL, LREAL, STRING,
DATE_AND_TIME, LTIME, TIME) e cade su un `SymbolObservationDefaultViewModel` read-only per i tipi non
gestiti. La lettura live usa `WhenValueChanged()` di `TwinCAT.Ads.Reactive` (notifiche ADS, non polling),
con log dedicato sul repository "observation". La scrittura usa `CreateVariableHandle` + `WriteAny`
(caso speciale `WriteAnyString` con `Encoding.Default`).

**`ExploreViewModel`** — ricerca reattiva ben congegnata: throttle **adattivo** (3 s se il termine è
< 5 caratteri, 400 ms se ≥ 5), `DistinctUntilChanged`, merge con il comando Invio, e
`search(searchTerm).TakeUntil(input)` per **cancellare la ricerca in corso** quando l'utente continua a
digitare. La ricerca è un `SymbolIterator` case-insensitive `Contains` sui simboli flat.

## 5. Build e rilascio

CI GitHub Actions (`.github/workflows/dotnet-core.yml`), su `windows-latest`, trigger su push a
`develop` / `features/**` e PR verso `master`/`develop`:
restore → build Release → `dotnet test` → **doppia publish** (single-file framework-dependent e
self-contained ReadyToRun) → zip → creazione **release come prerelease** con tag `1.2.<run_number>`
e upload dei due asset.

Branch: `master` (default), `develop`, `features/uiRefactor`. Ultimo tag della serie: `1.2.17`.

## 6. Osservazioni critiche

Rilevate leggendo il codice; nessuna modifica applicata.

1. **`.NET 5` end-of-life** (maggio 2022) e `RuntimeIdentifier` fissato a `win10-x64`. Migrazione a
   .NET 8/9 richiederebbe anche l'aggiornamento di ReactiveUI 9 (molto datato, oggi siamo alla 20.x).
2. **`dotnet test` in CI non testa nulla**: non esiste alcun progetto di test .NET nella solution —
   `Tests/` contiene solo il progetto PLC dummy. Il gate di qualità in CI è di fatto vuoto.
3. **`ViewModelLocator()` senza parametri va in `NullReferenceException`**: chiama `BindServices()`
   con `Kernel` non ancora assegnato (`ViewModelLocator.cs:17-27`). È usato solo dalle proprietà
   design-time `DesignInstanceCreator` / `DesignViewModelFactory`, quindi non impatta il runtime,
   ma rompe il designer XAML.
4. **Bug latente in `RestoreViewModel.DisplayVariables`** (`RestoreViewModel.cs:43-55`): il setter
   assegna a `liveVariables` invece che a `displayVariables`. Non si manifesta perché il codice usa
   solo `.Clear()`/`.AddRange()` sul getter, ma è una mina.
5. **`Encoding.Default` per le stringhe** (`SymbolObservationViewModel.cs`): dipende dalla code page di
   sistema; per stringhe PLC non-ASCII può produrre corruzione. Il PLC usa tipicamente Windows-1252.
6. **`CheckConnectionHealth` ogni secondo esegue `Client.ReadState()`** in modo sincrono su un thread
   del pool: con PLC lento o rete degradata può accumulare chiamate.
7. **Backup persistenti: gli array sono esclusi** dal filtro (`!InstancePath.Contains("[")`), quindi un
   backup non è necessariamente completo. Comportamento intenzionale ma non documentato nel README.
8. **`Restore` scrive senza validazione di tipo/struttura** contro i simboli attuali del PLC: se il JSON
   proviene da una versione precedente del programma PLC, gli errori emergono solo come eccezione per
   singola variabile (loggata + MessageBox). Non c'è dry-run né report riepilogativo.
9. `GuiModuleCatalog.Load()` è vuoto: i binding della GUI sono sparsi in `ViewModelLocator.BindServices()`.

## 7. Rilevanza per l'uso pratico

Utile come strumento operativo (backup/restore persistenti, watch e forzatura simboli senza aprire XAE)
e come **riferimento architetturale** per client ADS in .NET: la combinazione
`AdsClient` + `Reactive` + `SymbolLoaderFactory` + `ReadJson/WriteJson` di `TwinCAT.JsonExtension` è
il pattern più pulito presente nel codice, riutilizzabile in altri progetti di supervisione.
