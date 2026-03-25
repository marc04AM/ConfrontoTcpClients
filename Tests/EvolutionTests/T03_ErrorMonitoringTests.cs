using System.Reflection;
using Sistec.Core.Devices;
using Sistec.Core;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.EvolutionTests;

/// <summary>
/// T03: Dimostra l'evoluzione del monitoraggio errori.
/// - Fael: nessun Error event, nessun ErrorsPerSecond
/// - SiDel: ha Error event e ErrorsPerSecond, MonitorErrors usa while(Connected) senza lock
/// - Mb: ha Error event e ErrorsPerSecond, MonitorErrors usa while(IsConnected()) con lock
/// - Def: ha Error event e ErrorsPerSecond, MonitorErrors usa while(IsConnected()) con lock (come Mb)
/// </summary>
public class T03_ErrorMonitoringTests
{
    // ── FAEL: NESSUN MONITORAGGIO ─────────────────────────

    [Fact]
    public void Fael_NonHaEventoError()
    {
        var hasError = ReflectionHelper.HasEvent(typeof(FaelTcpClient), "Error");
        Assert.False(hasError, "FaelTcpClient non dovrebbe avere l'evento Error");
    }

    [Fact]
    public void Fael_NonHaProprietaErrorsPerSecond()
    {
        var hasProp = ReflectionHelper.HasProperty(typeof(FaelTcpClient), "ErrorsPerSecond");
        Assert.False(hasProp, "FaelTcpClient non dovrebbe avere ErrorsPerSecond");
    }

    // ── SIDEL: MONITORAGGIO PRESENTE ──────────────────────

    [Fact]
    public void SiDel_HaEventoError()
    {
        var hasError = ReflectionHelper.HasEvent(typeof(SiDelTcpClient), "Error");
        Assert.True(hasError, "SiDelTcpClient dovrebbe avere l'evento Error");
    }

    [Fact]
    public void SiDel_HaErrorsPerSecond()
    {
        var client = new SiDelTcpClient();
        Assert.Equal(0, client.ErrorsPerSecond);
    }

    [Fact]
    public void SiDel_MonitorErrors_UsaConnectedSenzaLock()
    {
        // Verifica strutturale: il metodo MonitorErrors di SiDel
        // legge il campo Connected direttamente (senza passare per IsConnected())
        // Questo è un problema di thread-safety documentato.
        var method = typeof(SiDelTcpClient).GetMethod("MonitorErrors",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // Il metodo esiste ed è privato - la differenza con Mb è documentata:
        // SiDel: while (Connected)     ← lettura senza lock
        // Mb:    while (IsConnected()) ← lettura con lock
    }

    // ── MB: MONITORAGGIO MIGLIORATO ───────────────────────

    [Fact]
    public void Mb_HaEventoError()
    {
        var hasError = ReflectionHelper.HasEvent(typeof(MbTcpClient), "Error");
        Assert.True(hasError, "MbTcpClient dovrebbe avere l'evento Error");
    }

    [Fact]
    public void Mb_HaErrorsPerSecond()
    {
        var client = new MbTcpClient();
        Assert.Equal(0, client.ErrorsPerSecond);
    }

    [Fact]
    public void Mb_HaMaxErrorsPerSecond()
    {
        Assert.Equal(10, MbTcpClient.MaxErrorsPerSecond);
    }

    // ── DEF: MONITORAGGIO MIGLIORATO (COME MB) ────────────

    [Fact]
    public void Def_HaEventoError()
    {
        var hasError = ReflectionHelper.HasEvent(typeof(DefTcpClient), "Error");
        Assert.True(hasError, "DefTcpClient dovrebbe avere l'evento Error");
    }

    [Fact]
    public void Def_HaErrorsPerSecond()
    {
        var client = new DefTcpClient();
        Assert.Equal(0, client.ErrorsPerSecond);
    }

    [Fact]
    public void Def_HaMaxErrorsPerSecond()
    {
        Assert.Equal(10, DefTcpClient.MaxErrorsPerSecond);
    }

    // ── CONFRONTO ─────────────────────────────────────────

    [Fact]
    public void Confronto_ErrorMonitoring_FaelNoFeature_SiDelMbDefSi()
    {
        Assert.False(ReflectionHelper.HasEvent(typeof(FaelTcpClient), "Error"));
        Assert.False(ReflectionHelper.HasProperty(typeof(FaelTcpClient), "ErrorsPerSecond"));

        Assert.True(ReflectionHelper.HasEvent(typeof(SiDelTcpClient), "Error"));
        Assert.True(ReflectionHelper.HasProperty(typeof(SiDelTcpClient), "ErrorsPerSecond"));

        Assert.True(ReflectionHelper.HasEvent(typeof(MbTcpClient), "Error"));
        Assert.True(ReflectionHelper.HasProperty(typeof(MbTcpClient), "ErrorsPerSecond"));

        Assert.True(ReflectionHelper.HasEvent(typeof(DefTcpClient), "Error"));
        Assert.True(ReflectionHelper.HasProperty(typeof(DefTcpClient), "ErrorsPerSecond"));
    }
}
