# Confronto delle clients TCP tra i 3 HMI

- SiDel
- MB
- FAEL

## Eventi

- OnConnect()
- OnDisconnect()

## Valutazione dell'evoluzione tramite unit test

Il progetto `Tests/` contiene 42 unit test (xUnit, .NET 8) che ripetono gli stessi scenari su tutti e 3 i client per dimostrarne l'evoluzione. Ogni test asserisce il **comportamento reale** del client, inclusi i bug noti.

### Matrice di maturita'

| Area | Test | Fael | SiDel | Mb |
|------|------|------|-------|-----|
| **Naming** | T01 | Nessuna proprieta' `Name` | Ha `Name`, ma il costruttore con parametro e' buggato (assegnamento commentato) | `Name` funzionante, costruttore corretto |
| **Thread Safety contatore** | T02 | Nessun contatore | `i++` non atomico: race condition sotto concorrenza | `Interlocked.Increment`: nomi sempre unici |
| **Error Monitoring** | T03 | Nessun evento `Error`, nessun `ErrorsPerSecond` | Presente, ma `MonitorErrors` legge `Connected` senza lock | Presente, `MonitorErrors` usa `IsConnected()` con lock |
| **InvalidOperationException su Read** | T04 | Disconnect aggressivo: chiama `Disconnect()` + `OnDisconnection()` | Soft error: incrementa `ErrorsPerSecond`, lancia `Error` sopra soglia | Soft error come SiDel + reset di `_pendingReadTask` |
| **Pending Read Reuse** | T05 | Assente | Assente | Presente: riusa il task in volo dopo timeout, evita letture concorrenti su `StreamReader` |
| **Disconnect Safety** | T06 | `_reader?.Dispose()` senza try/catch: puo' lanciare se I/O in corso | Idem: puo' lanciare | try/catch su Dispose: assorbe l'eccezione, disconnect sempre completo |
| **Structured Logging** | T07 | Logger statico globale (`Utilities.Logger`), string interpolation | Logger iniettabile via `Use()`, ma string interpolation | Logger iniettabile via `Use()`, template strutturati Serilog (`{Name}`, `{IpAddress}`) |

### Schema di evoluzione per ogni area

```
Fael (base)        ->  SiDel (intermedio)       ->  Mb (maturo)
---                    ---                          ---
Assente/Non gestito    Presente ma difettoso        Presente e corretto
```

### Salti evolutivi chiave

**Fael -> SiDel** (aggiunta di funzionalita'):
- Passa da disconnect aggressivo a gestione soft degli errori (T04)
- Aggiunge error monitoring con `ErrorsPerSecond` e evento `Error` (T03)
- Aggiunge naming delle istanze e logger iniettabile (T01, T07)

**SiDel -> Mb** (correzione bug e robustezza):
- Corregge il bug del costruttore con nome (T01)
- Rende il contatore thread-safe con `Interlocked.Increment` (T02)
- Introduce il pending read reuse per evitare `InvalidOperationException` da letture concorrenti su `StreamReader` (T05)
- Protegge il Disconnect con try/catch su Dispose (T06)
- Adotta logging strutturato Serilog con proprieta' tipizzate (T07)

### Esecuzione dei test

```bash
cd Tests
dotnet test --verbosity normal
```
