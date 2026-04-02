using Sistec.Core;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.DefClientTests;

/// <summary>
/// Test sul naming e i costruttori di DefTcpClient.
/// </summary>
public class D01_NamingTests
{
    [Fact]
    public void Costruttore_Default_AssegnaNomeAutomatico()
    {
        var client = new DefTcpClient();
        Assert.StartsWith("DefTcpClient_", client.Name);
    }

    [Fact]
    public void Costruttore_ConNome_UsaNomeFornito()
    {
        var client = new DefTcpClient("MyClient");
        Assert.Equal("MyClient", client.Name);
    }

    [Fact]
    public void Costruttore_Default_IncrementaContatore()
    {
        var c1 = new DefTcpClient();
        var c2 = new DefTcpClient();

        var num1 = int.Parse(c1.Name.Split('_')[1]);
        var num2 = int.Parse(c2.Name.Split('_')[1]);

        Assert.True(num2 > num1, "Il contatore deve incrementare tra istanze successive");
    }

    [Fact]
    public void Costruttore_ConNome_IncrementaComunqueContatore()
    {
        // Anche il costruttore con nome chiama this(), quindi incrementa il contatore
        var before = new DefTcpClient();
        var named = new DefTcpClient("Custom");
        var after = new DefTcpClient();

        var numBefore = int.Parse(before.Name.Split('_')[1]);
        var numAfter = int.Parse(after.Name.Split('_')[1]);

        // after deve avere numBefore + 2 (named ha consumato un contatore)
        Assert.Equal(numBefore + 2, numAfter);
    }

    [Fact]
    public void ContatoreIstanze_ThreadSafe_Interlocked()
    {
        // Verifica strutturale: il contatore usa Interlocked
        var field = typeof(DefTcpClient).GetField("_instanceCounter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal(typeof(int), field!.FieldType);
    }
}
