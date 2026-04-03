# Valutazione comparativa dei TcpClient

Analisi delle tre implementazioni: **Fael**, **Mb** e **SiDel**.

---

## 1. Thread Safety e Concorrenza

### Strategia di locking

| Aspetto | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| Lock object | `_lock` (readonly) | `_lock` (readonly) | `_lock` (readonly) | `_lock` (`Lock` .NET 9+) |
| `Disconnect()` sotto lock | Si | Si | Si | Si |
| `IsConnected()` sotto lock | Si | Si | Si | Si |
| `ConnectAsync()` sotto lock | **NO** | **NO** | **NO** | **NO** (limitazione mantenuta) |
| `ReadAsync()` / `WriteAsync()` sotto lock | **NO** | **NO** | **NO** | **NO** (cattura locale mitiga race) |

> **Problema comune**: `ConnectAsync()` non e' protetto da lock. Due chiamate concorrenti possono entrambe superare il check `IsConnected()` e creare connessioni duplicate con leak della prima.

### Disconnect() - Sicurezza del Dispose

| Aspetto | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| try/catch su Dispose reader/writer | **NO** | **SI** (righe 169-172) | **NO** | **SI** (da Mb) |

**Mb e' la versione piu' robusta**: se un `ReadAsync` e' in corso, il `Dispose()` dello `StreamReader` puo' lanciare `InvalidOperationException`. Fael e SiDel propagherebbero questa eccezione, lasciando potenzialmente il socket in stato inconsistente.

### Pattern `_pendingReadTask` (solo Mb)

`StreamReader.ReadAsync()` non supporta letture concorrenti. Se `ReadAsync()` va in timeout, Fael e SiDel **abbandonano** il task pendente e alla chiamata successiva ne creano uno **nuovo** sullo stesso `StreamReader`, causando `InvalidOperationException`.

**Mb risolve questo problema** (righe 200-215):

- Verifica se c'e' una lettura gia' in volo prima di lanciarne una nuova
- Riusa il task esistente se ancora pendente
- Resetta `_pendingReadTask = null` solo dopo completamento

**Impatto sugli altri**:

- **Fael**: alla `InvalidOperationException` fa `Disconnect()` + `OnDisconnection()` (riconnessione forzata) -- pesante ma resetta lo stato
- **SiDel**: incrementa `ErrorsPerSecond` senza risolvere la causa -- l'errore si ripete

### Race condition in Reconnect()

**Comune a tutti e tre**: se `Reconnect()` viene chiamato due volte rapidamente, il vecchio `_cancelReconnection` viene sovrascritto senza essere cancellato. Risultato: il primo tentativo continua in background senza possibilita' di cancellazione, e il primo CTS viene leakato.

### Contatore istanze

| Aspetto | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| Meccanismo | N/A (no Name) | `Interlocked.Increment` (thread-safe) | `i++` (**non atomico, race condition**) | `Interlocked.Increment` (da Mb) |

### MonitorErrors()

| Aspetto | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| Presente | NO | SI | SI | SI |
| Condizione loop | -- | `IsConnected()` (con lock, corretto) | `Connected` (senza lock/barriera, meno sicuro) | `IsConnected()` (da Mb) + riavvio dopo reconnect |

### Riepilogo Thread Safety

| Criterio | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| Protezione Dispose in Disconnect | Basica | **Migliore** | Basica | **Migliore** (da Mb) |
| Gestione read pendenti dopo timeout | Reconnessione forzata | **Riuso del task** | Errore ripetuto | **Riuso del task** (da Mb) |
| Contatore istanze thread-safe | N/A | **Si** | **No** | **Si** (da Mb) |
| MonitorErrors loop condition | N/A | **Con lock** | Senza barriera | **Con lock** + riavvio dopo reconnect |
| Race condition in Reconnect | Si | Si | Si | **No** (fix con lock + CompareExchange) |
| Race condition su _reader/_writer | Si (mitigata da check) | Si | Si | Si (mitigata da cattura locale) |

---

## 2. Gestione Errori e Resilienza

### Exception handling in ReadAsync

| Eccezione | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| `TimeoutException` | N/A | Task in volo, `Timeout` | N/A | Task in volo, `Timeout` |
| `IOException` | `OnDisconnection()` | `OnDisconnection()` + reset pending | `OnDisconnection()` | `OnDisconnection()` + reset pending |
| `ObjectDisposedException` | **NON gestita** | **SI** con filter `when (!Connected)` | **SI** con filter `when (!Connected)` | **SI** con filter `when (!Connected)` |
| `InvalidOperationException` | `Disconnect()` + `OnDisconnection()` | Incrementa `ErrorsPerSecond`, event `Error` se soglia | Incrementa `ErrorsPerSecond`, event `Error` se soglia | **Soft error** (come Mb) |
| `Exception` generica | Log + `ReadResult.Fail` | Log con stacktrace + inner exception | Log con stacktrace + inner exception | Log con stacktrace + inner exception |

### Exception handling in WriteAsync

| Eccezione | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| `OperationCanceledException` | N/A | N/A | N/A | `WriteResult.Timeout` |
| `InvalidOperationException` | **Catch dedicato** con `OnDisconnection()` | Catch generico | Catch generico | **Disconnessione** (come Fael) |
| `IOException` | `OnDisconnection()` | `OnDisconnection()` | `OnDisconnection()` | `OnDisconnection()` |
| Log dettagliato (stacktrace + inner) | NO | SI | SI | SI |

### Error monitoring

| Aspetto | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| `ErrorsPerSecond` / `MaxErrorsPerSecond` | **Assente** | Presente (soglia 10/s) | Presente (soglia 10/s) | Presente (soglia 10/s) |
| Evento `Error` | **Assente** | Presente | Presente | Presente |
| `MonitorErrors()` | **Assente** | `while (IsConnected())` | `while (Connected)` | `while (IsConnected())` + riavvio dopo reconnect |

### Flusso ConnectAsync e OnConnected

| Aspetto | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| `OnConnected` da `ConnectAsync(IPAddress)` | **SI** (riga 119) | NO | NO | **SI** (come Fael) |
| `OnConnected` da `ConnectAsync(string)` | NO (delega e basta) | **SI** (riga 97) | **SI** (riga 91) | NO (come Fael) |
| `OnConnected` dopo riconnessione | **NO** (bug) | **SI** (riga 278) | **SI** (riga 263) | **SI** (automatico via `ConnectAsync(IPAddress)`) |
| `MonitorErrors` avviato dopo connect | NO | SI | SI | **SI** (in `ConnectAsync(IPAddress)`, fix) |

> **Bug Fael**: dopo una riconnessione riuscita, i subscriber di `OnConnected` non vengono notificati.

### Logging

| Aspetto | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| Tipo logger | `Utilities.Logger` (statico) | `ILogger` iniettato via `Use()` | `ILogger` iniettato via `Use()` | `ILogger` iniettato via `Use()` |
| Formato | String interpolation `$""` | **Template strutturati Serilog** `"{Name}..."` | String interpolation `$""` | **Template strutturati Serilog** (da Mb) |
| Proprieta' `Name` nei log | Assente (solo IP:Port) | Presente | Presente | Presente |

> **Problema SiDel**: usa `$""` con Serilog, vanificando i vantaggi dello structured logging (indicizzazione, filtraggio). La stringa viene allocata prima che Serilog possa catturare i parametri.

### Reconnection policy

| Aspetto | Fael | Mb | SiDel |
| --- | --- | --- | --- |
| Default policy | `ReconnectionPolicy.Default` | `ExponentialBackoffReconnectionPolicy.Default` | `ExponentialBackoffReconnectionPolicy.Default` |

### Riepilogo Resilienza

| Criterio | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| `ObjectDisposedException` in Read | NO | **SI** | **SI** | **SI** |
| Error monitoring / rate-limiting | **Assente** | **Completo** | Presente ma meno robusto | **Completo** + riavvio dopo reconnect |
| `OnConnected` dopo reconnect | **NO (bug)** | SI | SI | **SI** (automatico) |
| Logging strutturato | NO | **SI** | NO (interpolazione) | **SI** (da Mb) |
| Logger iniettabile | NO | **SI** | SI | **SI** |

---

## 3. Design API e Qualita' del Codice

### Superficie pubblica e costruttori

| Aspetto | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| Proprieta' `Name` | Assente | Presente (counter `Interlocked`) | Presente ma **BUG nel costruttore** | Presente (da Mb) |
| `TcpClient()` | N/A | Auto-genera nome thread-safe | Auto-genera nome (`i++` non atomico) | Auto-genera nome thread-safe |
| `TcpClient(string name)` | N/A | Funzionante: `Name = name` | **BUG**: `{ }// => Name = name;` -- nome ignorato | Funzionante (da Mb) |
| `Use(ILogger)` | Assente | Presente | Presente | Presente |
| Evento `Error` | Assente | Presente | Presente | Presente |

> **Bug critico SiDel** (riga 33): il costruttore `TcpClient(string name)` ha l'assegnazione commentata. Il parametro `name` viene completamente ignorato.

### Nullability annotations

| Aspetto | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| Approccio | `?` annotations (corretto) | `null!` (sopprime warning) | **Nessuna annotazione** | `?` annotations (da Fael) |
| Coerenza Disconnect/null | `= null` (coerente) | `= null!` (nasconde nullita') | `= null` a campi non-nullable (warning) | `= null` (coerente, da Fael) |

- **Fael**: approccio piu' pulito, dichiarazioni nullable coerenti con l'uso
- **Mb**: `null!` sopprime i warning ma nasconde il fatto che i campi possono essere null a runtime
- **SiDel**: nessuna annotazione, causerebbe warning con `<Nullable>enable</Nullable>`

### Feature del linguaggio C#

| Aspetto | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| Lock type | `new()` (`object`, C# 9+) | `new object()` | `new object()` | `Lock` (.NET 9+) |
| `using` style | `await using` (moderno) | `using` | `using` | `using var` (dichiarativo) |
| Namespace | `Sistec.Core.Devices` | `Sistec.Core` | `Sistec.Core.Devices` | `Sistec.Core` |

### Naming conventions

| Aspetto | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| Buffer length | `_bufferLength` (con `_`, corretto) | `bufferLength` (senza `_`) | `bufferLength` (senza `_`) | `_bufferLength` (da Fael, non `static`) |
| TcpClient field | `_tcpClient` (descrittivo) | `_tcpc` (abbreviato) | `_tcpc` (abbreviato) | `_tcpClient` (da Fael) |
| Instance counter | N/A | `_instanceCounter` (descrittivo) | `i` (non descrittivo) | `_instanceCounter` (da Mb) |
| READ/WRITE_TIMEOUT | `public static` | `private static` | `public static` | **`private const`** |

### Codice commentato e residui

| Aspetto | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| Quantita' | Poco (Task.Run, Debug.Print) | Minima (1 riga duplicata in WriteAsync) | **Significativa** (costruttore, Warning, WriteAsync) | **Minima** |
| Bug da codice commentato | No | No | **SI** (costruttore nome rotto) | No |

### Dipendenze esterne

| Aspetto | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| Using aggiuntivi | Nessuno | `Sistec.Asyril.Utils` | Nessuno | Nessuno |
| Serilog diretto | No (usa Utilities) | Si (`using Serilog`) | Si (`using Serilog`) | Si (`using Serilog`) |

### Riepilogo Qualita' Codice

| Criterio | Fael | Mb | SiDel | **Definitivo** |
| --- | --- | --- | --- | --- |
| Sintassi C# moderna | **Migliore** | Intermedia | Intermedia | **Migliore** (da Fael + `Lock` .NET 9+) |
| Nullability | **Corretta** | Accettabile (`null!`) | **Assente** | **Corretta** (da Fael) |
| Naming conventions | **Consistente** | Quasi consistente | Inconsistente (`i`, no `_`) | **Consistente** (da Fael) |
| Codice commentato | Poco | **Minimo** | Significativo | **Minimo** |
| Bug funzionali | No | No | **SI** (costruttore) | No |
| Structured logging | No | **SI** | No (vanificato da `$""`) | **SI** (da Mb) |
| Timeout costanti | `public static` | `private static` | `public static` | **`private const`** |

---

## Verdetto finale

### Classifica per area (solo originali)

| Area | 1° posto | 2° posto | 3° posto |
| --- | --- | --- | --- |
| Thread Safety & Concorrenza | **Mb** | Fael | SiDel |
| Gestione Errori & Resilienza | **Mb** | SiDel | Fael |
| Design API & Qualita' Codice | **Mb** | Fael | SiDel |

### Sintesi per client

**Mb** - La versione piu' matura e completa tra gli originali. Punti di forza: `_pendingReadTask` per letture concorrenti, `Disconnect()` robusto con try/catch, structured logging Serilog, contatore thread-safe, `OnConnected` dopo reconnect, error monitoring con `IsConnected()`. Difetti minori: inconsistenza nell'uso di `Utilities.Logger` in `Reconnect()`, `null!` discutibile, naming `bufferLength` senza underscore.

**Fael** - La versione piu' minimale e sintatticamente moderna (C# 9+ con `new()`, `await using`). Nullability corretta. Pero' manca di: identita' dell'istanza (`Name`), error monitoring, logger iniettabile, `OnConnected` dopo reconnect (bug), gestione `ObjectDisposedException`. Non gestisce le letture pendenti dopo timeout se non con riconnessione forzata.

**SiDel** - La versione piu' problematica. Ha la stessa struttura di Mb ma con difetti significativi: **bug nel costruttore** (nome ignorato), contatore `i++` non thread-safe, logging con `$""` che vanifica Serilog, `Disconnect()` senza protezione, `MonitorErrors` con condizione non thread-safe, e residui di codice commentato. Rappresenta probabilmente una versione precedente di Mb non ancora aggiornata.

### Versione definitiva

La **versione definitiva** (`TcpClient.cs`) supera tutti e tre gli originali in ogni area. Unisce:

- **Da Fael**: sintassi C# moderna, nullable annotations corrette, naming consistente
- **Da Mb**: pending read, dispose sicuro, structured logging, contatore thread-safe
- **Da SiDel/Mb**: `Name`, `Use(ILogger)`, evento `Error`, error monitoring

Corregge 4 bug comuni (race condition in `Reconnect`, `_bufferLength` statico, `MonitorErrors` non riavviato dopo reconnect, CTS non disposto in `ConnectAsync`) e adotta una **strategia ibrida** per `InvalidOperationException`: soft error in `ReadAsync` (il `_pendingReadTask` permette recovery) e disconnessione in `WriteAsync` (stream irrecuperabile senza pending write). Usa inoltre `Lock` (.NET 9+) e timeout `private const`.
