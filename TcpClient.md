# TcpClient — Versione Definitiva

Versione unificata che combina le migliori feature di Fael, SiDel e Mb, con fix aggiuntivi per bug presenti in tutte e tre le implementazioni originali.

---

## Provenienza delle feature

| Feature | Origine | Motivazione |
| --- | --- | --- |
| Sintassi C# moderna (`new()`, `await using`) | Fael | Codice piu' conciso e idiomatico per .NET 8 |
| Nullable annotations corrette (`?`) | Fael | Le dichiarazioni riflettono la reale nullabilita' a runtime, a differenza di `null!` (Mb) che sopprime i warning |
| Naming consistente (`_bufferLength`, `_tcpClient`) | Fael | Rispetta le convenzioni .NET: `_` per campi privati, nomi descrittivi |
| Guard `!Connected` in `ReadAsync`/`WriteAsync` | Fael | Fail-fast: evita di lanciare operazioni su stream null |
| `InvalidOperationException` dedicato in `WriteAsync` | Fael | Disconnessione esplicita anziche' errore generico silenzioso |
| `_pendingReadTask` (riuso task in volo) | Mb | Risolve `InvalidOperationException` da letture concorrenti su `StreamReader` |
| `Disconnect()` con try/catch su Dispose | Mb | Evita crash se un'operazione asincrona e' in corso durante il dispose |
| `Interlocked.Increment` per contatore istanze | Mb | Thread-safe, a differenza di `i++` (SiDel) |
| `MonitorErrors()` con `IsConnected()` | Mb | Usa il metodo con lock anziche' il flag `Connected` (SiDel) |
| `OnConnected` dopo riconnessione | Mb/SiDel | Notifica i subscriber anche dopo una riconnessione riuscita (bug Fael) |
| Structured logging Serilog | Mb | Template `"{Name}..."` anziche' interpolazione `$""` (SiDel) |
| `Name` property | SiDel/Mb | Identita' dell'istanza nei log e nel debug |
| `Use(ILogger)` | SiDel/Mb | Logger iniettabile anziche' statico globale (Fael) |
| Evento `Error` con rate-limiting | SiDel/Mb | Notifica quando `ErrorsPerSecond` supera `MaxErrorsPerSecond` |
| `ExponentialBackoffReconnectionPolicy` | SiDel/Mb | Backoff esponenziale anziche' policy lineare (Fael) |
| `ObjectDisposedException` handling | SiDel/Mb | Gestione graceful quando lo stream viene disposto durante la lettura |

---

## Bug fix rispetto agli originali

Oltre alle feature combinate, la versione definitiva corregge bug presenti in **tutte e tre** le implementazioni.

### 1. Race condition in `Reconnect()` — CTS sovrascritto

**Presente in**: Fael, SiDel, Mb

**Problema**: se `Reconnect()` viene chiamato due volte rapidamente, `_cancelReconnection` viene sovrascritto senza cancellare il vecchio. Il primo tentativo continua in background senza possibilita' di cancellazione e il primo CTS viene leakato.

Inoltre, la lambda del `Task.Run` accedeva a `_cancelReconnection` come campo di istanza: nella parte finale (`_cancelReconnection?.Dispose()`) poteva disporre il **nuovo** CTS creato da una seconda chiamata a `Reconnect()`.

**Fix**:

```csharp
// Cancella il vecchio CTS prima di sovrascriverlo
_cancelReconnection?.Cancel();
_cancelReconnection?.Dispose();

var cts = new CancellationTokenSource();
_cancelReconnection = cts;
Task.Run(async () =>
{
    // ... usa cts (variabile locale catturata, non il campo) ...
    cts.Dispose();
    // Annulla il riferimento solo se e' ancora il nostro CTS
    Interlocked.CompareExchange(ref _cancelReconnection, null, cts);
});
```

### 2. `_bufferLength` era `static` — condiviso tra istanze

**Presente in**: Fael, SiDel, Mb

**Problema**: `_bufferLength` veniva aggiornato con `_tcpClient.ReceiveBufferSize` ad ogni connessione. Essendo `static`, due istanze connesse a server con `ReceiveBufferSize` diversi si sovrascrivevano a vicenda il buffer.

**Fix**: cambiato da `private static int` a `private int` (campo di istanza).

### 3. `MonitorErrors()` non riavviato dopo riconnessione

**Presente in**: SiDel, Mb (Fael non ha MonitorErrors)

**Problema**: `_ReconnectAsync` chiama `ConnectAsync(IPAddress)`, che non invoca `MonitorErrors()`. Il monitor si avvia solo in `ConnectAsync(string)`. Dopo una disconnessione il loop di `MonitorErrors` esce (perche' `IsConnected()` diventa `false`), e dopo la riconnessione non viene mai riavviato.

**Fix**: aggiunta chiamata a `MonitorErrors()` nel blocco post-riconnessione di `Reconnect()`.

### 4. CTS in `ConnectAsync` non disposto sul path di successo

**Presente in**: Fael, SiDel, Mb

**Problema**: il `CancellationTokenSource` creato per il timeout della connessione non veniva mai disposto se la connessione andava a buon fine. Minor memory leak (il GC lo raccoglie comunque) ma cattiva pratica con risorse `IDisposable`.

**Fix**: aggiunto `finally { cts.Dispose(); }` al blocco try/catch della connessione.

---

## API Pubblica

### Costruttori

```csharp
// Nome auto-generato: "TcpClient_0", "TcpClient_1", ...
var client = new TcpClient();

// Nome personalizzato
var client = new TcpClient("MyDevice");
```

Il contatore usa `Interlocked.Increment` (thread-safe). Il costruttore con nome funziona correttamente (fix del bug SiDel dove il parametro veniva ignorato).

### Proprieta'

| Proprieta' | Tipo | Descrizione |
| --- | --- | --- |
| `Name` | `string` | Identificativo dell'istanza, usato nei log |
| `Connected` | `bool` | Flag di connessione (senza lock) |
| `Port` | `int` | Porta di connessione |
| `ConnectionTimeout` | `int` | Timeout in ms per la connessione |
| `Encoding` | `Encoding?` | Encoding usato per reader/writer (default UTF-8) |
| `ReconnectionPolicy` | `IReconnectionPolicy` | Policy di riconnessione (default: exponential backoff) |
| `ErrorsPerSecond` | `int` | Contatore errori corrente (resettato ogni secondo) |
| `MaxErrorsPerSecond` | `static int` | Soglia oltre la quale viene invocato l'evento `Error` (default: 10) |

### Metodi

| Metodo | Ritorno | Descrizione |
| --- | --- | --- |
| `ConnectAsync(string, int, int, Encoding?)` | `Task<ConnectResult>` | Connessione con indirizzo IP come stringa |
| `ConnectAsync(IPAddress, int, int, Encoding?)` | `Task<ConnectResult>` | Connessione con `IPAddress` |
| `Disconnect()` | `void` | Disconnessione sicura sotto lock |
| `IsConnected()` | `bool` | Stato connessione thread-safe (sotto lock) |
| `ReadAsync(int)` | `Task<ReadResult>` | Lettura con timeout (default 1000ms) |
| `WriteAsync(string, int)` | `Task<WriteResult>` | Scrittura con timeout (default 3000ms) |
| `Reconnect()` | `void` | Avvia riconnessione asincrona secondo la policy |
| `CancelReconnection()` | `void` | Cancella il tentativo di riconnessione in corso |
| `Use(ILogger)` | `void` | Inietta il logger Serilog |

### Eventi

| Evento | Quando |
| --- | --- |
| `OnConnected` | Dopo connessione o riconnessione riuscita |
| `Disconnected` | Alla disconnessione (prima della riconnessione automatica) |
| `ConnectionFail` | Se la connessione fallisce (timeout, errore, ecc.) |
| `Reconnecting` | Ad ogni tentativo di riconnessione |
| `Error` | Quando `ErrorsPerSecond` supera `MaxErrorsPerSecond` |

---

## Flusso degli eventi

### Prima connessione

```text
ConnectAsync(string)
  └─ ConnectAsync(IPAddress) → connessione TCP
       └─ successo → MonitorErrors() → OnConnected
       └─ fallimento → ConnectionFail
```

### Disconnessione e riconnessione

```text
errore I/O in ReadAsync/WriteAsync
  └─ OnDisconnection()
       ├─ Disconnected (evento)
       └─ Reconnect()
            └─ Task.Run → ReconnectAgent
                 └─ _ReconnectAsync() → Reconnecting (evento) → ConnectAsync(IPAddress)
                      └─ successo → MonitorErrors() → OnConnected
                      └─ fallimento → ritenta secondo ReconnectionPolicy
```

### Ciclo di lettura (a carico del chiamante)

```csharp
var client = new TcpClient("MyDevice");
client.Use(logger);
client.OnConnected += _ => Console.WriteLine("Connesso");
client.Disconnected += _ => Console.WriteLine("Disconnesso");

var result = await client.ConnectAsync("192.168.1.100", 5000);
if (!result.IsConnected) return;

while (client.IsConnected())
{
    var read = await client.ReadAsync();
    if (read.IsSuccess)
        ProcessMessage(read.Data!);
}
```

---

## Gestione eccezioni

### ReadAsync

| Eccezione | Comportamento |
| --- | --- |
| `IOException` | `OnDisconnection()` → riconnessione automatica |
| `ObjectDisposedException` (quando `!Connected`) | Log + `ReadResult.Fail` — disconnessione volontaria dell'utente |
| `InvalidOperationException` | `ErrorsPerSecond++`, evento `Error` se soglia superata, `ReadResult.Fail` |
| Altre | Log con stacktrace + inner exception, `ReadResult.Fail` |

### WriteAsync

| Eccezione | Comportamento |
| --- | --- |
| `InvalidOperationException` | Log + `OnDisconnection()` → riconnessione automatica |
| `IOException` | `OnDisconnection()` → riconnessione automatica |
| Altre | Log con stacktrace + inner exception, `WriteResult.Fail` |

### Pending Read — protezione letture concorrenti

`StreamReader.ReadAsync()` non supporta letture concorrenti. Se una `ReadAsync` va in timeout, il task di lettura resta in volo. Alla chiamata successiva, anziche' crearne uno nuovo (che causerebbe `InvalidOperationException`), viene **riutilizzato** il task pendente con un nuovo timeout:

```csharp
if (_pendingReadTask == null || _pendingReadTask.IsCompleted)
{
    _pendingReadTask = _reader!.ReadAsync(_pendingBuffer, 0, _pendingBuffer.Length);
}
// ri-attende il task esistente con il nuovo timeout
```

---

## Thread Safety

| Aspetto | Meccanismo |
| --- | --- |
| `Disconnect()` | Sotto `lock(_lock)` — atomico rispetto a `IsConnected()` |
| `IsConnected()` | Sotto `lock(_lock)` — legge `Connected` e `_tcpClient.Connected` atomicamente |
| Dispose reader/writer | try/catch `InvalidOperationException` — tollera operazioni async in corso |
| Contatore istanze | `Interlocked.Increment` |
| `MonitorErrors` loop | Usa `IsConnected()` (con lock) anziche' il flag `Connected` |
| `Reconnect()` CTS | Variabile locale catturata + `Interlocked.CompareExchange` per cleanup |

### Limitazioni note

- `ConnectAsync()` non e' protetto da lock: due chiamate concorrenti possono entrambe superare `IsConnected()` e creare connessioni duplicate. Questa e' una limitazione ereditata da tutti e tre gli originali e mantenuta per compatibilita'.
- `ReadAsync`/`WriteAsync` non sono sotto lock: accedono a `_reader`/`_writer` che possono essere nullati da un `Disconnect()` concorrente. Il pattern `!Connected` guard + null-forgiving `_reader!` mitiga ma non elimina la finestra di race.

---

## Confronto riassuntivo

| Criterio | Fael | SiDel | Mb | Definitivo |
| --- | --- | --- | --- | --- |
| Sintassi C# moderna | **Si** | No | No | **Si** |
| Nullable annotations corrette | **Si** | No | Parziale (`null!`) | **Si** |
| Naming conventions | **Consistente** | Inconsistente | Quasi consistente | **Consistente** |
| Nome istanza | No | Bug | **Si** | **Si** |
| Logger iniettabile | No | **Si** | **Si** | **Si** |
| Structured logging Serilog | No | No | **Si** | **Si** |
| Pending read (no read concorrenti) | No | No | **Si** | **Si** |
| Dispose sicuro in Disconnect | No | No | **Si** | **Si** |
| Error monitoring thread-safe | No | Parziale | **Si** | **Si** |
| OnConnected dopo reconnect | No (bug) | **Si** | **Si** | **Si** |
| MonitorErrors dopo reconnect | No | No | No | **Si** (fix) |
| `_bufferLength` per istanza | No | No | No | **Si** (fix) |
| Race condition Reconnect | Si | Si | Si | **No** (fix) |
| CTS dispose in ConnectAsync | No | No | No | **Si** (fix) |
