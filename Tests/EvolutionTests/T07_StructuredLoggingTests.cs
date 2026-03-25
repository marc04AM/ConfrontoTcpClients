using System.Reflection;
using Sistec.Core.Devices;
using Sistec.Core;
using Sistec.Core.Utils;
using TcpClientEvolution.Tests.Helpers;
using TcpClientEvolution.Tests.Stubs;
using Xunit;

namespace TcpClientEvolution.Tests.EvolutionTests;

/// <summary>
/// T07: Dimostra l'evoluzione del logging.
/// - Fael: logger statico globale (Utilities.Logger), string interpolation
/// - SiDel: logger iniettabile via Use(), ma usa ancora string interpolation ($"...")
/// - Mb: logger iniettabile via Use(), usa template strutturati Serilog ("{Name}", "{IpAddress}")
///
/// La differenza chiave: con i template strutturati, Serilog può estrarre
/// proprietà tipizzate (Name, IpAddress, Port) per ricerche e filtri.
/// Con l'interpolazione, il messaggio è già "baked" in una stringa.
/// </summary>
public class T07_StructuredLoggingTests : IAsyncLifetime
{
    private TcpListenerHelper _listener = null!;

    public async Task InitializeAsync()
    {
        _listener = new TcpListenerHelper();
    }

    public async Task DisposeAsync()
    {
        await _listener.DisposeAsync();
    }

    // ── FAEL: LOGGER STATICO ─────────────────────────────

    [Fact]
    public void Fael_NonHaMetodoUse()
    {
        // Fael usa Utilities.Logger (statico globale), non ha Use()
        var useMethod = typeof(FaelTcpClient).GetMethod("Use",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.Null(useMethod);
    }

    [Fact]
    public async Task Fael_UsaLoggerStaticoGlobale_ConInterpolazione()
    {
        var (logger, sink) = FakeLoggerFactory.Create();
        Utilities.Logger = logger;

        try
        {
            var acceptTask = _listener.AcceptClientAsync();
            var client = new FaelTcpClient();
            await client.ConnectAsync(_listener.Address, _listener.Port);
            await acceptTask;

            // Fael logga con string interpolation → il template contiene già i valori
            Assert.True(sink.Events.Count > 0, "Fael deve loggare eventi tramite Utilities.Logger");

            var connectEvent = sink.Events.FirstOrDefault(e =>
                e.MessageTemplate.Text.Contains("Connected to"));
            Assert.NotNull(connectEvent);

            // Con interpolazione, il template NON ha placeholder {IpAddress}
            Assert.DoesNotContain("{IpAddress}", connectEvent!.MessageTemplate.Text);
            // Le proprietà strutturate non sono presenti
            Assert.False(connectEvent.Properties.ContainsKey("IpAddress"),
                "Fael non usa template strutturati → nessuna proprietà IpAddress");
        }
        finally
        {
            Utilities.Logger = null;
        }
    }

    // ── SIDEL: LOGGER INIETTABILE MA INTERPOLATO ─────────

    [Fact]
    public void SiDel_HaMetodoUse()
    {
        var useMethod = typeof(SiDelTcpClient).GetMethod("Use",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(useMethod);
    }

    [Fact]
    public async Task SiDel_UsaLoggerIniettato_ConInterpolazione()
    {
        var (logger, sink) = FakeLoggerFactory.Create();
        var client = new SiDelTcpClient();
        client.Use(logger);

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        Assert.True(sink.Events.Count > 0, "SiDel deve loggare eventi");

        var connectEvent = sink.Events.FirstOrDefault(e =>
            e.MessageTemplate.Text.Contains("Connected to"));
        Assert.NotNull(connectEvent);

        // SiDel usa $"..." interpolation → template è una stringa pre-risolta
        // Non ha placeholder strutturati
        Assert.DoesNotContain("{IpAddress}", connectEvent!.MessageTemplate.Text);
        Assert.False(connectEvent.Properties.ContainsKey("IpAddress"),
            "SiDel non usa template strutturati → nessuna proprietà IpAddress");
    }

    // ── MB: LOGGER INIETTABILE CON TEMPLATE STRUTTURATI ──

    [Fact]
    public void Mb_HaMetodoUse()
    {
        var useMethod = typeof(MbTcpClient).GetMethod("Use",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(useMethod);
    }

    [Fact]
    public async Task Mb_UsaLoggerIniettato_ConTemplateStrutturati()
    {
        var (logger, sink) = FakeLoggerFactory.Create();
        var client = new MbTcpClient();
        client.Use(logger);

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        Assert.True(sink.Events.Count > 0, "Mb deve loggare eventi");

        var connectEvent = sink.Events.FirstOrDefault(e =>
            e.MessageTemplate.Text.Contains("{IpAddress}"));
        Assert.NotNull(connectEvent);

        // MB usa template strutturati → il template contiene placeholder
        Assert.Contains("{Name}", connectEvent!.MessageTemplate.Text);
        Assert.Contains("{IpAddress}", connectEvent.MessageTemplate.Text);
        Assert.Contains("{Port}", connectEvent.MessageTemplate.Text);

        // Le proprietà sono estratte come oggetti tipizzati
        Assert.True(connectEvent.Properties.ContainsKey("Name"),
            "Mb template strutturato deve avere proprietà Name");
        Assert.True(connectEvent.Properties.ContainsKey("IpAddress"),
            "Mb template strutturato deve avere proprietà IpAddress");
        Assert.True(connectEvent.Properties.ContainsKey("Port"),
            "Mb template strutturato deve avere proprietà Port");
    }

    // ── CONFRONTO ────────────────────────────────────────

    [Fact]
    public async Task Confronto_Logging_FaelStatico_SiDelInterpolato_MbStrutturato()
    {
        var (logger, sink) = FakeLoggerFactory.Create();

        // Fael: usa Utilities.Logger
        Utilities.Logger = logger;
        var fael = new FaelTcpClient();
        var accept1 = _listener.AcceptClientAsync();
        await fael.ConnectAsync(_listener.Address, _listener.Port);
        await accept1;

        int faelEventCount = sink.Events.Count;
        var faelHasStructured = sink.Events.Any(e => e.MessageTemplate.Text.Contains("{IpAddress}"));

        // SiDel: usa Use()
        sink.Events.Clear();
        var sidel = new SiDelTcpClient();
        sidel.Use(logger);
        var accept2 = _listener.AcceptClientAsync();
        await sidel.ConnectAsync(_listener.AddressString, _listener.Port);
        await accept2;

        var sidelHasStructured = sink.Events.Any(e => e.MessageTemplate.Text.Contains("{IpAddress}"));

        // Mb: usa Use() con template
        sink.Events.Clear();
        var mb = new MbTcpClient();
        mb.Use(logger);
        var accept3 = _listener.AcceptClientAsync();
        await mb.ConnectAsync(_listener.AddressString, _listener.Port);
        await accept3;

        var mbHasStructured = sink.Events.Any(e => e.MessageTemplate.Text.Contains("{IpAddress}"));

        // Risultati
        Assert.True(faelEventCount > 0, "Fael logga tramite Utilities.Logger");
        Assert.False(faelHasStructured, "Fael: interpolazione, no template strutturati");
        Assert.False(sidelHasStructured, "SiDel: interpolazione, no template strutturati");
        Assert.True(mbHasStructured, "Mb: template strutturati con {IpAddress}");

        Utilities.Logger = null;
    }
}
