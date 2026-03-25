# TcpClient — Release Note

## Riepilogo

Miglioramenti di thread-safety, robustezza e correttezza applicati a `TcpClient.cs` rispetto alla versione di Sidel (`a0e8593d` → `6335f1dc` nel repo di MB).

## Modifiche

### 1. Contatore istanze thread-safe

| Prima | Dopo |
| --- | --- |
| `private static int i = 0;` | `private static int _instanceCounter = 0;` |
| `Name = $"TcpClient_{i++}"` | `Name = $"TcpClient_{Interlocked.Increment(ref _instanceCounter) - 1}"` |

`i++` non è atomico: due thread che creano istanze contemporaneamente possono ottenere lo stesso numero. `Interlocked.Increment` elimina la race condition.

### 2. Costruttore con nome — bug fix

| Prima | Dopo |
| --- | --- |
| `public TcpClient(string name) : this() { }// => Name = name;` | `public TcpClient(string name) : this() { Name = name; }` |

Il parametro `name` veniva completamente ignorato (assegnamento commentato). Ora viene effettivamente assegnato.

### 3. Pending read task — riuso del task in volo (modifica principale)

Nella versione originale, `ReadAsync` creava **ogni volta** un nuovo `_reader.ReadAsync(...)`. Se il timeout scadeva, il task restava in volo e al giro successivo ne veniva creato un altro, causando **letture concorrenti su `StreamReader`** (che non le supporta).

La nuova versione introduce i campi `_pendingReadTask` e `_pendingBuffer`:

- Se un task precedente è ancora in volo (`!IsCompleted`), viene **ri-atteso** con un nuovo timeout invece di avviarne uno nuovo.
- Solo quando il task completa, viene azzerato (`_pendingReadTask = null`) e il buffer viene letto.
- In tutti i blocchi `catch`, `_pendingReadTask` viene azzerato per evitare di ri-attendere un task fallito.

Questo elimina la race condition sullo `StreamReader` e previene `InvalidOperationException` da letture concorrenti.

### 4. `MonitorErrors` — uso di `IsConnected()` al posto di `Connected`

| Prima | Dopo |
| --- | --- |
| `while (Connected)` | `while (IsConnected())` |

`Connected` è una lettura diretta del campo senza lock. `IsConnected()` è protetto da `_lock` e verifica sia `Connected` che `_tcpc?.Connected`, dando un risultato più affidabile.

### 5. `Disconnect` — protezione contro `InvalidOperationException`

| Prima | Dopo |
| --- | --- |
| `_reader?.Dispose();` | `try { _reader?.Dispose(); } catch (InvalidOperationException) { log; }` |
| `_writer?.Dispose();` | `try { _writer?.Dispose(); } catch (InvalidOperationException) { log; }` |

Se un `ReadAsync` o `WriteAsync` è in corso quando si chiama `Disconnect`, il `Dispose` può lanciare `InvalidOperationException`. I try/catch evitano che il disconnect fallisca a metà, lasciando risorse non rilasciate (socket, stream).

### 6. Commento esplicativo su `_stream?.Close()`

Aggiunto `// chiude il socket → interrompe i pending I/O` per documentare che la chiusura dello stream è intenzionale e serve a sbloccare le operazioni asincrone pendenti.

## Commit di riferimento (nel repo MB)

| Commit | Descrizione |
| --- | --- |
| `a0e8593d` | Sidel: `TcpClient` funzionale ma senza protezioni thread-safety |
| `6335f1dc` | Thread safety, test coverage, and doc improvements |
