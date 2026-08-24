# Backup e restore delle variabili persistenti — riscrittura

Branch: `fix/persistent-performance-reporting`

## I due problemi

**1. Lentezza (10 minuti con array di strutture).**
Il tool delegava a `TwinCAT.JsonExtension` (`ReadJson`/`WriteJson`), la cui `ReadRecursive`
scende fino a ogni foglia e per ognuna esegue `ReadSymbol` + `ReadValue`: **due round-trip ADS
per foglia**. Un `ARRAY[1..500] OF ST_Dati` con 20 membri = 10.000 foglie = **~20.000 telegrammi
sequenziali**. Aggravante lato client: `iterator.Contains(s.Parent)` dentro un `Where`, cioè
O(n²) con ri-enumerazione dell'albero dei simboli a ogni simbolo.

**2. Variabili perse senza segnalazione.**
Il difetto era **nel backup, non nel restore**. In `PersistentVariableService.cs:66-69` il
`catch` per variabile scriveva una riga di log e la variabile **non entrava nel JSON**. Il file
si salvava come se fosse completo. Al restore la variabile non c'era, quindi non veniva scritta
e non c'era nulla da segnalare.

## Come è stato risolto

Ogni variabile persistente viene ora trasferita **intera** (la libreria ADS ricostruisce l'albero
dei valori lato client) e le variabili sono raggruppate in **sum command**, che trasferiscono un
batch in un solo telegramma. I sum command restituiscono anche un **codice di errore per singolo
simbolo**: è ciò che rende possibile il reporting completo.

Ordine di grandezza atteso: da ~2 telegrammi per foglia a ~1 telegramma per batch di variabili.

Ogni variabile persistente produce ora un esito — scritta, fallita con il suo errore ADS, oppure
saltata con il motivo — e backup e restore mostrano il report nell'interfaccia.

## Componenti nuovi

| File | Ruolo |
|------|-------|
| `Interfaces/Models/VariableOperationResult.cs` | esito di una singola variabile |
| `Interfaces/Models/PersistentOperationReport.cs` | report complessivo, `IsComplete`, `Details()` |
| `Interfaces/Values/IPlcValueNode.cs` | astrazione dell'albero dei valori (rende testabile la conversione) |
| `Logic/Values/PlcJsonConverter.cs` | conversione valore ↔ JSON, con raccolta dei mismatch |
| `Logic/Values/ValueCoercion.cs` | adattamento dei tipi JSON ai tipi PLC |
| `Logic/Values/DynamicValueNode.cs` | adapter su `DynamicValue` della libreria ADS |
| `Logic/Services/PersistentSymbolScanner.cs` | individua le variabili persistenti radice, O(n) |
| `Logic/Services/JsonPathBuilder.cs` | costruzione dell'albero JSON dai path puntati |
| `Logic/Services/PersistentVariableReader.cs` | backup con sum command e fallback |
| `Logic/Services/PersistentVariableWriter.cs` | restore con sum command e fallback |

## Altri difetti corretti

- **Path con nomi ripetuti**: `InstancePath.Replace("." + localName, "")` sostituisce *tutte* le
  occorrenze, non l'ultimo segmento. `GVL.Axis.Axis` veniva collassato su `GVL` e il valore
  finiva nel nodo sbagliato. Ora il path viene diviso sui separatori.
- **Coercizione dei tipi**: JSON conosce solo `long`, `double`, `bool` e `string`. Senza
  conversione al tipo dichiarato, il restore fallirebbe su ogni `INT`, `BYTE` o `DT`.
- **Timestamp PlcOpen**: `DT.Date` restituisce `DateTimeOffset` e riespone sempre l'ora locale.
  La conversione preserva ora l'**istante**, non la rappresentazione, anche fra fusi diversi.
- **Array**: lunghezze diverse fra file e PLC vengono riportate e si scrive l'intersezione,
  invece di fallire o troncare in silenzio.

## Cosa è verificato e cosa no

**Verificato su macOS** — 48 test unitari verdi (`Tests/TwinCatAdsTool.Logic.Tests`):
conversione JSON, costruzione dei path, coercizione dei tipi, gestione dei mismatch.
`TwinCatAdsTool.Logic` e `.Interfaces` compilano senza errori né warning.

**Verificato dopo l'aggiornamento a .NET 8** (branch `upgrade/net8-modern-ui`): con quel branch
la GUI si compila anche da macOS, quindi le modifiche a `BackupViewModel`, `RestoreViewModel` e
alle due view compilano senza errori. Sul solo branch dei fix, che è ancora .NET 5, non erano
compilabili fuori da Windows.

**Da validare sul campo** (richiede PLC):
1. tempo di backup prima/dopo sullo stesso impianto;
2. confronto del JSON prodotto dalla versione vecchia e dalla nuova sullo stesso PLC — il
   formato dovrebbe coincidere, ma i tipi data/ora sono il punto da controllare per primo;
3. restore su un PLC con struttura modificata, per vedere il report elencare i mismatch.

## Note

- Il progetto di test **non è nella solution**: targetta un framework più recente di quello che
  l'SDK 5.0 della CI può compilare. Va integrato con l'aggiornamento a .NET 8.
  Esecuzione manuale: `dotnet test Tests/TwinCatAdsTool.Logic.Tests`
- `ReadGlobalPersistentVariables` resta come `[Obsolete]` per compatibilità.
- Difetto adiacente **non toccato** (fuori scope): il setter di `RestoreViewModel.DisplayVariables`
  assegna a `liveVariables` invece che a `displayVariables`. Latente, perché il codice usa solo
  il getter.
