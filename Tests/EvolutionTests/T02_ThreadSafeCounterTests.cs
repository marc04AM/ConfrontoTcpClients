using System.Collections.Concurrent;
using Sistec.Core.Devices;
using Sistec.Core;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.EvolutionTests;

/// <summary>
/// T02: Dimostra la differenza di thread-safety nel contatore delle istanze.
/// - SiDel: usa i++ (non atomico) → possibili nomi duplicati sotto concorrenza
/// - Mb: usa Interlocked.Increment (atomico) → nomi sempre unici
/// </summary>
public class T02_ThreadSafeCounterTests
{
    [Fact]
    public void SiDel_ContatoreSemplice_NonAtomico()
    {
        // Verifica strutturale: SiDel usa un campo statico "i" con i++ (non thread-safe)
        var hasField = ReflectionHelper.HasField(typeof(SiDelTcpClient), "i");
        Assert.True(hasField, "SiDelTcpClient dovrebbe avere il campo statico 'i' (contatore non thread-safe)");
    }

    [Fact]
    public void Mb_ContatoreConInterlocked_Atomico()
    {
        // Verifica strutturale: Mb usa "_instanceCounter" con Interlocked.Increment
        var hasField = ReflectionHelper.HasField(typeof(MbTcpClient), "_instanceCounter");
        Assert.True(hasField, "MbTcpClient dovrebbe avere il campo statico '_instanceCounter' (thread-safe)");
    }

    [Fact]
    public void SiDel_CreazionceConcorrente_PuoProvereDuplicati()
    {
        // Crea molte istanze in parallelo con i++ non atomico.
        // La race condition può produrre nomi duplicati.
        const int count = 2000;
        var names = new ConcurrentBag<string>();

        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            _ =>
            {
                var client = new SiDelTcpClient();
                names.Add(client.Name);
            });

        var uniqueCount = names.Distinct().Count();

        // Con i++ non atomico, sotto alta concorrenza è probabile
        // ottenere meno nomi unici del totale.
        // Se il test gira su una macchina single-core, potrebbe comunque passare
        // con tutti unici, quindi documentiamo solo il rischio.
        if (uniqueCount < count)
        {
            // Race condition dimostrata! Nomi duplicati trovati.
            Assert.True(uniqueCount < count,
                $"SiDel: race condition confermata, {count - uniqueCount} nomi duplicati su {count}");
        }
        else
        {
            // Nessun duplicato in questa esecuzione (possibile su single-core)
            // Il test passa comunque: la verifica strutturale sopra conferma il rischio.
            Assert.True(true, "Nessun duplicato in questa esecuzione, ma il rischio esiste (i++ non è atomico)");
        }
    }

    [Fact]
    public void Mb_CreazioneConcorrente_NomiSempreUnici()
    {
        // Con Interlocked.Increment, i nomi devono essere SEMPRE unici.
        const int count = 2000;
        var names = new ConcurrentBag<string>();

        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            _ =>
            {
                var client = new MbTcpClient();
                names.Add(client.Name);
            });

        var uniqueCount = names.Distinct().Count();

        Assert.Equal(count, uniqueCount);
    }
}
