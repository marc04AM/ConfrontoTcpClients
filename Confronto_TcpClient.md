# Confronto delle 3 implementazioni di TcpClient

## Panoramica

| Caratteristica | _Fael.cs | _SiDel.cs | _Mb.cs | **Definitivo** |
| --- | --- | --- | --- | --- |
| **Namespace** | `Sistec.Core.Devices` | `Sistec.Core.Devices` | `Sistec.Core` | `Sistec.Core` |
| **Using extra** | - | - | `Sistec.Asyril.Utils` | - |
| **Nullable** | Si (C# moderno, `?`) | No (stile pre-nullable) | Si (C# moderno, `?`) | Si (`?`, da Fael) |
| **Logger** | `Utilities.Logger` (statico) | `ILogger _logger` (istanza) | `ILogger _logger` (istanza) | `ILogger` (istanza, da Mb) |
| **Nome istanza** | Nessuno | `Name` (con contatore `i++`) | `Name` (con `_instanceCounter` thread-safe) | `Name` (`Interlocked`, da Mb) |
| **Evento Error** | No | Si | Si | Si |
| **MonitorErrors** | No | Si | Si | Si (con fix: anche dopo reconnect) |
| **Pending Read** | No | No | Si | Si (da Mb) |
| **Lock type** | `object` | `object` | `object` | `Lock` (.NET 9+) |

---

## 1. Namespace e dipendenze

- **_Fael** e **_SiDel**: usano `Sistec.Core.Devices`
- **_Mb**: usa `Sistec.Core` e aggiunge il riferimento a `Sistec.Asyril.Utils`

## 2. Naming e identificazione istanze

| | _Fael | _SiDel | _Mb | **Definitivo** |
| --- | --- | --- | --- | --- |
| Proprietà `Name` | Assente | Presente | Presente | Presente |
| Contatore istanze | Nessuno | `static int i = 0` (non thread-safe, `i++`) | `static int _instanceCounter` (thread-safe, `Interlocked.Increment`) | `Interlocked.Increment` (da Mb) |
| Costruttore con nome | Assente | Presente ma **il parametro `name` viene ignorato** (commentato) | Presente e **funzionante** (`Name = name`) | Funzionante (da Mb) |

**Nota importante**: in `_SiDel.cs` il costruttore `TcpClient(string name)` ha il corpo commentato (`// => Name = name;`), quindi il nome passato viene scartato.

## 3. Sistema di logging

- **_Fael**: usa il logger statico globale `Utilities.Logger` con string interpolation diretta (`$"..."`)
- **_SiDel**: usa un `ILogger _logger` di istanza iniettato via `Use(ILogger)`, ma con **string interpolation** (`$"..."`) — non sfrutta i template strutturati di Serilog
- **_Mb**: usa un `ILogger _logger` di istanza iniettato via `Use(ILogger)`, con **template strutturati di Serilog** (`"{Name} Trying to connect to {IpAddress}:{Port}"`) — approccio corretto per logging semantico

## 4. Gestione errori e monitoraggio

### Evento `Error` e `MonitorErrors`

- **_Fael**: non ha ne l'evento `Error` ne il monitoraggio errori
- **_SiDel** e **_Mb**: entrambi hanno `ErrorsPerSecond`, `MaxErrorsPerSecond`, evento `Error` e metodo `MonitorErrors()`

### Differenza nel ciclo di MonitorErrors

- **_SiDel**: il loop controlla `while (Connected)` — usa solo il flag booleano
- **_Mb**: il loop controlla `while (IsConnected())` — usa il metodo thread-safe con lock

### Gestione eccezioni in ReadAsync

| Eccezione | _Fael | _SiDel | _Mb | **Definitivo** |
| --- | --- | --- | --- | --- |
| `TimeoutException` | N/A (no pending) | N/A (no pending) | Task in volo, `ReadResult.Timeout` | Task in volo, `ReadResult.Timeout` |
| `IOException` | `Disconnect()` + `OnDisconnection()` | Solo `OnDisconnection()` | Solo `OnDisconnection()` | `OnDisconnection()` |
| `InvalidOperationException` | `Disconnect()` + `OnDisconnection()`, ritorna `NotConnected` | `ErrorsPerSecond++`, ritorna `Fail` | `ErrorsPerSecond++`, ritorna `Fail` | **Soft error** (`ErrorsPerSecond++`, `Fail`) — come Mb |
| `ObjectDisposedException` | Non gestita separatamente | Gestita con `when (!Connected)` | Gestita con `when (!Connected)` | Gestita con `when (!Connected)` |

### Gestione eccezioni in WriteAsync

| Eccezione | _Fael | _SiDel | _Mb | **Definitivo** |
| --- | --- | --- | --- | --- |
| `OperationCanceledException` | N/A | N/A | N/A | `WriteResult.Timeout` (CancellationToken) |
| `InvalidOperationException` | `OnDisconnection()` | Catch generico, no disconnessione | Catch generico, no disconnessione | **Disconnessione** (`OnDisconnection()`) — come Fael |
| `IOException` | `OnDisconnection()` | `OnDisconnection()` | `OnDisconnection()` | `OnDisconnection()` |
| Altre | `WriteResult.Fail` | Log dettagliato + `Fail` | Log dettagliato + `Fail` | Log dettagliato + `Fail` |

**Strategia ibrida della versione definitiva**: in ReadAsync `InvalidOperationException` e' un **soft error** (come Mb) perche' il meccanismo `_pendingReadTask` permette il recovery. In WriteAsync e' una **disconnessione** (come Fael) perche' senza pending write lo stream e' compromesso.

## 5. Pending Read (solo _Mb)

`_Mb.cs` introduce un meccanismo di **pending read** assente nelle altre due versioni:

```csharp
private Task<int>? _pendingReadTask;
private char[]?    _pendingBuffer;
```

Se una `ReadAsync` va in timeout, il task di lettura non viene abbandonato. Alla chiamata successiva viene **riutilizzato** lo stesso task. Questo risolve il problema per cui `StreamReader` non supporta letture concorrenti: senza questo meccanismo, un timeout lascia un read "orfano" e la successiva `ReadAsync` lancia una `InvalidOperationException`.

## 6. Connessione e evento OnConnected

| | _Fael | _SiDel | _Mb | **Definitivo** |
| --- | --- | --- | --- | --- |
| `OnConnected` invocato in | `ConnectAsync(IPAddress)` (alla fine) | `ConnectAsync(string)` (dopo il risultato) | `ConnectAsync(string)` (dopo il risultato) | `ConnectAsync(IPAddress)` (come Fael) |
| `MonitorErrors` avviato in | Mai | `ConnectAsync(string)` | `ConnectAsync(string)` | `ConnectAsync(IPAddress)` (fix) |
| Dopo riconnessione | `OnConnected` invocato (perché `_ReconnectAsync` chiama `ConnectAsync(IPAddress)` che lo contiene) | `OnConnected` invocato nel `Reconnect` | `OnConnected` invocato nel `Reconnect` | `OnConnected` invocato automaticamente (come Fael) |

**Differenza critica**: in `_Fael`, `OnConnected` viene invocato dentro `ConnectAsync(IPAddress)`, il che significa che viene chiamato anche durante la riconnessione (perche `_ReconnectAsync` chiama `ConnectAsync`). In `_SiDel`/`_Mb`, `OnConnected` e invocato in `ConnectAsync(string)` per la prima connessione e in `Reconnect()` per le successive — ma `ConnectAsync(IPAddress)` non lo invoca.

## 7. Disconnect — gestione dispose sicura

- **_Fael** e **_SiDel**: chiamano `_reader?.Dispose()` e `_writer?.Dispose()` direttamente. Se c'e un'operazione asincrona in corso, puo lanciare `InvalidOperationException` non gestita
- **_Mb**: wrappa le dispose in `try/catch(InvalidOperationException)` con logging, evitando crash durante dispose concorrenti

## 8. Nullabilita e stile del codice

- **_Fael**: usa la sintassi C# moderna con nullable reference types (`?` su eventi e campi), `new()` senza tipo, `await using`
- **_SiDel**: stile pre-nullable, nessun `?` sugli eventi, usa `using` classico (non `await using`)
- **_Mb**: usa nullable reference types, `null!` per i campi non-nullable inizializzati dopo la costruzione, `using` classico

## 9. Reconnection Policy

- **_Fael**: usa `Sistec.Core.Utils.ReconnectionPolicy.Default`
- **_SiDel** e **_Mb**: usano `ExponentialBackoffReconnectionPolicy.Default`

## 10. Campo TcpClient interno

- **_Fael**: `_tcpClient` (nome completo)
- **_SiDel** e **_Mb**: `_tcpc` (abbreviato)

---

## 11. Flusso degli eventi: OnConnected, Disconnected e messaggi

### OnConnected

| | _Fael | _SiDel | _Mb |
| --- | --- | --- | --- |
| **Prima connessione** | Invocato in `ConnectAsync(IPAddress)` alla fine del metodo | Invocato in `ConnectAsync(string)` dopo il risultato | Invocato in `ConnectAsync(string)` dopo il risultato |
| **Riconnessione** | Invocato di nuovo perche `_ReconnectAsync` chiama `ConnectAsync(IPAddress)` che lo contiene | Invocato esplicitamente in `Reconnect()` dopo verifica `IsConnected()` | Invocato esplicitamente in `Reconnect()` dopo verifica `IsConnected()` |

**Differenza chiave**:

- **_Fael**: `OnConnected` sta dentro `ConnectAsync(IPAddress)` — viene sempre invocato, sia alla prima connessione che durante la riconnessione. Non c'e distinzione tra i due scenari.
- **_SiDel** e **_Mb**: `OnConnected` e separato in due punti distinti:
  - `ConnectAsync(string)` per la prima connessione (il metodo con `string` chiama quello con `IPAddress`, poi invoca `OnConnected` solo se ha avuto successo)
  - `Reconnect()` dopo che `reconnectAgent` ha completato con successo e `IsConnected()` e `true`

  Questo significa che `ConnectAsync(IPAddress)` — usato internamente da `_ReconnectAsync` — **non** invoca `OnConnected`. L'evento viene gestito dal chiamante.

### Disconnected

Il flusso e identico in tutte e 3 le versioni:

```text
errore I/O o eccezione → OnDisconnection() → Disconnected?.Invoke(this) → Reconnect()
```

La differenza sta in **quali eccezioni** scatenano la disconnessione:

| Eccezione | _Fael | _SiDel/_Mb | **Definitivo** |
| --- | --- | --- | --- |
| `IOException` (Read/Write) | `OnDisconnection()` | `OnDisconnection()` | `OnDisconnection()` |
| `InvalidOperationException` (Read) | `Disconnect()` + `OnDisconnection()`, ritorna `NotConnected` | **No disconnessione** — solo `ErrorsPerSecond++`, ritorna `Fail` | **Soft error** (come Mb) |
| `InvalidOperationException` (Write) | `OnDisconnection()` | **No disconnessione** — ritorna `WriteResult.Fail(e)` | **Disconnessione** (come Fael) |

**_Fael** e la piu aggressiva: qualsiasi `InvalidOperationException` causa disconnessione e riconnessione. **_SiDel** e **_Mb** la trattano come errore soft (contatore errori). La **versione definitiva** adotta un approccio ibrido: soft error in lettura (dove `_pendingReadTask` permette il recovery), disconnessione in scrittura (dove lo stream e' irrecuperabile).

### Messaggi (Read/Write) — nessun evento dedicato

Nessuna delle 3 versioni ha un **evento OnMessage**. La lettura e pull-based: il chiamante deve fare polling manualmente con un loop esterno:

```csharp
while (tcpClient.IsConnected())
{
    var result = await tcpClient.ReadAsync();
    if (result.IsSuccess) { /* gestisci il messaggio */ }
}
```

### Schema riassuntivo del flusso

```mermaid
flowchart TD
    A[ConnectAsync] --> B[OnConnected]
    B --> C[Loop esterno del chiamante]
    C --> D[ReadAsync / WriteAsync]
    D --> E[Eccezione I/O]
    E --> F[OnDisconnection]
    F --> G[Disconnected]
    F --> H[Reconnect]
    H --> I[_ReconnectAsync]
    I --> J[ConnectAsync]
    J -->|successo| B
```

---

## Riepilogo: evoluzione del codice

```text
_Fael (base) → _SiDel (aggiunge naming, error monitoring, logger di istanza)
                   → _Mb (aggiunge pending read, logging strutturato, dispose sicuro, thread safety)
```

| Miglioria | _Fael | _SiDel | _Mb | **Definitivo** |
| --- | :---: | :---: | :---: | :---: |
| Logger di istanza iniettabile | - | X | X | X |
| Nome istanza | - | X | X | X |
| Contatore thread-safe | - | - | X | X |
| Costruttore con nome funzionante | - | - | X | X |
| Monitoraggio errori/secondo | - | X | X | X |
| Pending read (no read concorrenti) | - | - | X | X |
| Logging strutturato Serilog | - | - | X | X |
| Dispose sicuro nel Disconnect | - | - | X | X |
| MonitorErrors thread-safe | - | - | X | X |
| Backoff esponenziale riconnessione | - | X | X | X |
| MonitorErrors dopo reconnect | - | - | - | X (fix) |
| `_bufferLength` per istanza | - | - | - | X (fix) |
| Race condition Reconnect fix | - | - | - | X (fix) |
| CTS dispose in ConnectAsync | - | - | - | X (fix) |
| `InvalidOpEx` Write → disconnessione | X | - | - | X (ibrido) |
| Lock type (.NET 9+) | - | - | - | X |
| Timeout `private const` | - | - | - | X |

**`_Mb.cs` rappresenta la versione piu matura tra gli originali**. La **versione definitiva** unisce il meglio di tutti e tre, corregge 4 bug comuni e adotta una strategia ibrida per `InvalidOperationException` (soft error in lettura, disconnessione in scrittura).
