# Confronto TcpClient.cs vs TcpClientFael2.cs

Due versioni "definitive" dello stesso TcpClient. Condividono la stessa architettura (pending read, reconnect con CTS safe, MonitorErrors, eventi) ma differiscono per target framework, logging e alcuni comportamenti chiave.

## Riepilogo differenze

| Aspetto | TcpClient.cs | TcpClientFael2.cs |
| --- | --- | --- |
| Namespace | `Sistec.Core` | `Sistec.Core.Devices` |
| Target | .NET 10 | netstandard2.1 |
| Lock | `Lock` (tipo .NET 9+) | `object` |
| Logging | `ILogger _logger` iniettato via `Use(ILogger)` | `Utilities.Logger` statico globale |
| Property encoding | `StreamEncoding` | `Encoding` (shadow sul tipo `System.Text.Encoding`) |
| ReconnectionPolicy default | `ExponentialBackoffReconnectionPolicy.Default` | `Utils.ReconnectionPolicy.Default` |
| Dipendenza Serilog | `using Serilog` esplicito | Nessun using diretto (passa da `Utilities`) |
| Metodo `Use(ILogger)` | Presente | Assente |

## Differenze nel dettaglio

### 1. Target framework e API async

**TcpClient.cs** usa API moderne disponibili da .NET 6/9/10:

```csharp
// ConnectAsync — CancellationToken nativo (.NET 6+)
await _tcpClient.ConnectAsync(_ipAddress, Port, cts.Token);

// ReadAsync — Task.WaitAsync (.NET 6+)
var count = await _pendingReadTask.WaitAsync(TimeSpan.FromMilliseconds(timeout));

// WriteAsync — Memory<char> + CancellationToken (.NET 6+)
await writer.WriteAsync(command.AsMemory(), cts.Token);

// Lock type (.NET 9+)
private readonly Lock _lock = new();
```

**TcpClientFael2.cs** usa il pattern `Task.WhenAny` per compatibilita' con netstandard2.1:

```csharp
// ConnectAsync — Task.WhenAny
var connectTask = _tcpClient.ConnectAsync(_ipAddress, Port);
var completed = await Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, cts.Token));
if (completed != connectTask) cts.Token.ThrowIfCancellationRequested();
await connectTask;

// ReadAsync — Task.WhenAny
var timeoutTask = Task.Delay(timeout);
var completed = await Task.WhenAny(_pendingReadTask, timeoutTask);
if (completed != _pendingReadTask) throw new TimeoutException();

// WriteAsync — Task.WhenAny
var writeTask = writer.WriteAsync(command);
var writeCompleted = await Task.WhenAny(writeTask, Task.Delay(Timeout.Infinite, cts.Token));

// object lock (pre-.NET 9)
private readonly object _lock = new();
```

**Impatto**: TcpClient.cs e' piu' pulito e performante (evita allocazioni extra di `Task.Delay`), ma richiede .NET 10. TcpClientFael2.cs gira su qualsiasi runtime che supporti netstandard2.1 (.NET Core 3.0+, .NET 5+, Xamarin, Unity).

### 2. Logging: dependency injection vs statico

**TcpClient.cs** — logger per-istanza iniettato:

```csharp
private ILogger? _logger;
public void Use(ILogger logger) => _logger = logger;

// Uso:
_logger?.Information("{Name} Connected to {IpAddress}:{Port}", Name, _ipAddress, Port);
```

**TcpClientFael2.cs** — logger statico globale:

```csharp
// Uso diretto:
Utilities.Logger?.Information("{Name} Connected to {IpAddress}:{Port}", Name, _ipAddress, Port);
```

**Impatto**: TcpClient.cs permette logger diversi per istanza (utile in scenari multi-tenant o test), non ha dipendenze statiche e facilita il testing. TcpClientFael2.cs e' piu' semplice da usare (zero configurazione) ma accoppia tutte le istanze allo stesso logger globale.

### 3. Evento OnConnected: posizionamento diverso

**TcpClient.cs** — invocato nell'overload `string` (riga 122):

```csharp
// ConnectAsync(string, ...)
var result = await ConnectAsync(ip, port, timeout, streamEncoding ?? Encoding.UTF8);
if (result.IsConnected)
{
    MonitorErrors();
    OnConnected?.Invoke(this);  // <-- qui
}

// ConnectAsync(IPAddress, ...) — NON invoca OnConnected
```

**TcpClientFael2.cs** — invocato nell'overload `IPAddress` (riga 179):

```csharp
// ConnectAsync(string, ...) — NON invoca OnConnected
var result = await ConnectAsync(ip, port, timeout, streamEncoding ?? Encoding.UTF8);
if (result.IsConnected)
{
    MonitorErrors();
    // nessun OnConnected qui
}

// ConnectAsync(IPAddress, ...)
Connected = true;
OnConnected?.Invoke(this);  // <-- qui
```

**Impatto**: In TcpClient.cs, chi chiama direttamente `ConnectAsync(IPAddress, ...)` non riceve l'evento `OnConnected`. In TcpClientFael2.cs l'evento scatta sempre, indipendentemente da quale overload viene usato. **TcpClientFael2.cs e' piu' corretto** su questo punto.

### 4. InvalidOperationException in ReadAsync

**TcpClient.cs** (righe 264-271) — conteggia l'errore senza disconnettere:

```csharp
catch (InvalidOperationException e)
{
    _pendingReadTask = null;
    ErrorsPerSecond++;
    _logger?.Debug("...", Name, e.Message);
    if (ErrorsPerSecond > MaxErrorsPerSecond)
        Error?.Invoke(this, new ErrorEventArgs(e));
    return ReadResult.Fail(e);
}
```

**TcpClientFael2.cs** (righe 269-275) — disconnette e avvia la riconnessione:

```csharp
catch (InvalidOperationException e)
{
    _pendingReadTask = null;
    Utilities.Logger?.Debug("...", Name, e.Message);
    Disconnect();
    OnDisconnection();
    return ReadResult.NotConnected();
}
```

**Impatto**: TcpClient.cs e' piu' tollerante — accumula errori e notifica solo dopo la soglia. TcpClientFael2.cs e' piu' aggressivo — tratta la `InvalidOperationException` come uno stato corrotto e forza la riconnessione. In ambienti industriali dove lo stream corrotto non si recupera, l'approccio di TcpClientFael2.cs e' probabilmente piu' sicuro.

### 5. Property `Encoding` vs `StreamEncoding`

**TcpClient.cs**:
```csharp
public Encoding? StreamEncoding { get; private set; }
```

**TcpClientFael2.cs**:
```csharp
public Encoding? Encoding { get; private set; }
// Richiede qualificazione esplicita:
Encoding = streamEncoding ?? System.Text.Encoding.UTF8;
```

**Impatto**: `StreamEncoding` evita l'ambiguita' con il tipo `System.Text.Encoding`. Il naming di TcpClient.cs e' preferibile.

## Codice identico

Le seguenti aree sono sostanzialmente identiche tra le due versioni:

- Costruttori e naming delle istanze (`TcpClient_0`, `TcpClient_1`, ...)
- Delegate ed eventi (`ConnectionFail`, `Disconnected`, `OnConnected`, `Reconnecting`, `Error`)
- `MonitorErrors()` — loop asincrono con `IsConnected()` guard
- `_ReconnectAsync()` — delega a `ConnectAsync(IPAddress, ...)`
- `OnDisconnection()` — invoca `Disconnected` + `Reconnect()`
- `Disconnect()` — lock + close/dispose con try/catch per operazioni concorrenti
- `IsConnected()` — lock + doppio check `Connected && _tcpClient?.Connected`
- Logica pending read in `ReadAsync` (riuso task in volo)
- `Reconnect()` — fix race condition con `Cancel/Dispose` + `Interlocked.CompareExchange`

## Verdetto

| Criterio | Migliore |
| --- | --- |
| Compatibilita' runtime | TcpClientFael2.cs (netstandard2.1) |
| Pulizia API async | TcpClient.cs (.NET 10 nativo) |
| Logging / testabilita' | TcpClient.cs (DI per-istanza) |
| Naming property | TcpClient.cs (`StreamEncoding`) |
| Evento OnConnected | TcpClientFael2.cs (scatta sempre) |
| Gestione InvalidOp in Read | TcpClientFael2.cs (disconnette, piu' sicuro) |

**TcpClient.cs** e' il design migliore in termini di architettura (DI, naming, API moderne), ma ha un bug nell'evento `OnConnected` mancante dal path `IPAddress` e una gestione piu' permissiva della `InvalidOperationException` in `ReadAsync`.

**TcpClientFael2.cs** e' piu' robusto nei comportamenti runtime (evento sempre emesso, riconnessione aggressiva) e compatibile con piu' piattaforme, ma usa un logger statico globale e un nome di property ambiguo.

La versione ideale prenderebbe l'architettura di TcpClient.cs con i due fix comportamentali di TcpClientFael2.cs.
