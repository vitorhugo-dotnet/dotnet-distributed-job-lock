using BillingWorker.Jobs;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace BillingWorker.Tests;

/// <summary>
/// O teste que justifica o projeto: varias instancias, o mesmo job store, um
/// unico trigger — e uma unica execucao.
/// </summary>
public class ClusteredSchedulerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Folga depois da primeira execucao, para uma segunda indevida ter chance de aparecer.</summary>
    private static readonly TimeSpan Folga = TimeSpan.FromSeconds(5);

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        await postgres.ResetQuartzAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ApenasUmaInstanciaExecutaOTrigger()
    {
        await using var instanciaA = await CriarInstanciaAsync("worker-a");
        await using var instanciaB = await CriarInstanciaAsync("worker-b");
        await using var instanciaC = await CriarInstanciaAsync("worker-c");

        // Sem isto o teste seria uma mentira confortavel: se o Quartz devolvesse
        // o mesmo scheduler para as tres, nao haveria cluster nenhum para provar.
        Assert.Equal(
            ["worker-a", "worker-b", "worker-c"],
            new[] { instanciaA, instanciaB, instanciaC }.Select(i => i.Scheduler.SchedulerInstanceId).Order());

        // As tres se registram em qrtz_scheduler_state, que so e preenchida com
        // clustering ligado — e o que faz uma instancia saber que as outras
        // existem e detectar quando uma morre.
        await EsperarInstanciasNoClusterAsync(3);

        await instanciaA.Scheduler.TriggerJob(BillingCloseJob.Key);

        await EsperarExecucoesAsync(1);
        await Task.Delay(Folga);

        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM job_run WHERE job_name = 'billing-close'"));
        Assert.Equal(25, await ContarAsync("SELECT count(*) FROM invoices WHERE status = 'CLOSED'"));
    }

    [Fact]
    public async Task DonoDaExecucaoEUmaDasInstanciasDoCluster()
    {
        await using var instanciaA = await CriarInstanciaAsync("worker-a");
        await using var instanciaB = await CriarInstanciaAsync("worker-b");

        await instanciaA.Scheduler.TriggerJob(BillingCloseJob.Key);
        await EsperarExecucoesAsync(1);

        var run = await new JobRunRepository(postgres.DataSource).FindLastAsync(BillingCloseJob.JobName, default);

        Assert.Contains(run!.Owner, new[] { "worker-a", "worker-b" });
        Assert.Equal("SUCCESS", run.Status);
    }

    [Fact]
    public async Task ReiniciarUmaInstanciaNaoDuplicaProcessamento()
    {
        await using var permanente = await CriarInstanciaAsync("worker-permanente");

        var reiniciavel = await CriarInstanciaAsync("worker-reiniciavel");
        await reiniciavel.Scheduler.TriggerJob(BillingCloseJob.Key);
        await EsperarExecucoesAsync(1);
        await reiniciavel.DisposeAsync();

        await using var reiniciada = await CriarInstanciaAsync("worker-reiniciavel");
        await reiniciada.Scheduler.TriggerJob(BillingCloseJob.Key);
        await EsperarExecucoesAsync(2);
        await Task.Delay(Folga);

        // A segunda execucao aconteceu, mas nao teve nada para fazer: o
        // fechamento e idempotente, entao reiniciar nao cobra ninguem de novo.
        Assert.Equal(25, await ContarAsync("SELECT count(*) FROM invoices WHERE status = 'CLOSED'"));
        Assert.Equal(0, await ContarAsync(
            """
            SELECT processed_items FROM job_run
             WHERE job_name = 'billing-close' AND status = 'SUCCESS'
             ORDER BY started_at DESC, id DESC LIMIT 1
            """));
    }

    private async Task<InstanciaWorker> CriarInstanciaAsync(string id)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["Worker:Id"] = id,
                // Cron que nao dispara durante o teste: o unico gatilho e o TriggerJob.
                ["Billing:Cron"] = "0 0 5 * * ?",
            })
            .Build();

        var services = new ServiceCollection();
        // O Quartz guarda o log provider num campo estatico do processo. Se cada
        // instancia trouxesse a propria LoggerFactory, a primeira a ser
        // descartada deixaria as seguintes com um ObjectDisposedException.
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddBillingServices(configuration);
        services.AddBillingScheduler(configuration);

        var provider = services.BuildServiceProvider();
        var scheduler = await provider.GetRequiredService<ISchedulerFactory>().GetScheduler();
        await scheduler.Start();

        return new InstanciaWorker(provider, scheduler);
    }

    private async Task EsperarInstanciasNoClusterAsync(int esperadas)
    {
        var limite = DateTime.UtcNow + Timeout;

        while (DateTime.UtcNow < limite)
        {
            if (await ContarAsync("SELECT count(*) FROM qrtz_scheduler_state") >= esperadas)
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"Apenas {await ContarAsync("SELECT count(*) FROM qrtz_scheduler_state")} de {esperadas} "
            + "instancias apareceram em qrtz_scheduler_state — o clustering nao esta ligado.");
    }

    private async Task EsperarExecucoesAsync(int esperadas)
    {
        var limite = DateTime.UtcNow + Timeout;

        while (DateTime.UtcNow < limite)
        {
            var concluidas = await ContarAsync(
                "SELECT count(*) FROM job_run WHERE job_name = 'billing-close' AND finished_at IS NOT NULL");

            if (concluidas >= esperadas)
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"O job nao concluiu {esperadas} execucao(oes) em {Timeout.TotalSeconds}s.");
    }

    private async Task<int> ContarAsync(string sql)
    {
        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(sql);
    }

    private sealed record InstanciaWorker(ServiceProvider Provider, IScheduler Scheduler) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Scheduler.Shutdown(waitForJobsToComplete: true);
            await Provider.DisposeAsync();
        }
    }
}
