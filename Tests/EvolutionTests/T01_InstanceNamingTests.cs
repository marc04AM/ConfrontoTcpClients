using Sistec.Core.Devices;
using Sistec.Core;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.EvolutionTests;

/// <summary>
/// T01: Dimostra l'evoluzione del naming delle istanze.
/// - Fael: nessuna proprietà Name
/// - SiDel: ha Name, ma il costruttore con parametro è buggato (nome ignorato)
/// - Mb: ha Name, costruttore corretto
/// </summary>
public class T01_InstanceNamingTests
{
    // ── FAEL ──────────────────────────────────────────────

    [Fact]
    public void Fael_NonHaProprietaName()
    {
        // Fael non espone una proprietà Name
        var hasName = ReflectionHelper.HasProperty(typeof(FaelTcpClient), "Name");
        Assert.False(hasName, "FaelTcpClient non dovrebbe avere la proprietà Name");
    }

    // ── SIDEL ─────────────────────────────────────────────

    [Fact]
    public void SiDel_CostruttoreDefault_AssegnaNomeAutomatico()
    {
        var client = new SiDelTcpClient();
        Assert.StartsWith("TcpClient_", client.Name);
    }

    [Fact]
    public void SiDel_CostruttoreConNome_BUG_NomeIgnorato()
    {
        // BUG DOCUMENTATO: il parametro name viene ignorato
        // perché l'assegnamento è commentato nel codice sorgente:
        //   public SiDelTcpClient(string name) : this() { }// => Name = name;
        var client = new SiDelTcpClient("DispositivoCustom");

        Assert.NotEqual("DispositivoCustom", client.Name);
        Assert.StartsWith("TcpClient_", client.Name); // usa il nome auto-generato
    }

    // ── MB ────────────────────────────────────────────────

    [Fact]
    public void Mb_CostruttoreDefault_AssegnaNomeAutomatico()
    {
        var client = new MbTcpClient();
        Assert.StartsWith("TcpClient_", client.Name);
    }

    [Fact]
    public void Mb_CostruttoreConNome_FIX_NomeAssegnatoCorrettamente()
    {
        // FIX: il costruttore assegna correttamente il nome:
        //   public MbTcpClient(string name) : this() { Name = name; }
        var client = new MbTcpClient("DispositivoCustom");

        Assert.Equal("DispositivoCustom", client.Name);
    }

    // ── CONFRONTO DIRETTO ─────────────────────────────────

    [Fact]
    public void Confronto_CostruttoreConNome_SiDelBuggato_MbCorretto()
    {
        const string nomeDesiderato = "MioDispositivo";

        var sidel = new SiDelTcpClient(nomeDesiderato);
        var mb = new MbTcpClient(nomeDesiderato);

        // SiDel ignora il nome → bug
        Assert.NotEqual(nomeDesiderato, sidel.Name);

        // Mb lo assegna correttamente → fix
        Assert.Equal(nomeDesiderato, mb.Name);
    }
}
