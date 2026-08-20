using BillingWorker.Billing;
using BillingWorker.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BillingWorker.Tests;

public class BillingCloseJobTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private JobRunRepository _runs = null!;
    private BillingCloseJob _job = null!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        _runs = new JobRunRepository(postgres.DataSource);
        _job = new BillingCloseJob(
            new BillingCloseService(postgres.DataSource),
            _runs,
            IdentidadeDe("worker-teste"),
            NullLogger<BillingCloseJob>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FechaFaturasEAuditaExecucao()
    {
        await _job.RunAsync(CancellationToken.None);

        var run = await _runs.FindLastAsync(BillingCloseJob.JobName, default);

        Assert.Equal("SUCCESS", run!.Status);
        Assert.Equal(25, run.ProcessedItems);
        Assert.Equal("worker-teste", run.Owner);
    }

    [Fact]
    public async Task SegundaExecucaoRegistraZeroProcessados()
    {
        await _job.RunAsync(CancellationToken.None);
        await _job.RunAsync(CancellationToken.None);

        var run = await _runs.FindLastAsync(BillingCloseJob.JobName, default);

        Assert.Equal("SUCCESS", run!.Status);
        Assert.Equal(0, run.ProcessedItems);
    }

    [Fact]
    public async Task RegistraFalhaQuandoOFechamentoQuebra()
    {
        await using var bancoInvalido = NpgsqlDataSource.Create(
            postgres.ConnectionString.Replace("Database=billing", "Database=nao_existe"));

        var jobQuebrado = new BillingCloseJob(
            new BillingCloseService(bancoInvalido),
            _runs,
            IdentidadeDe("worker-teste"),
            NullLogger<BillingCloseJob>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => jobQuebrado.RunAsync(CancellationToken.None));

        var run = await _runs.FindLastAsync(BillingCloseJob.JobName, default);

        Assert.Equal("FAILED", run!.Status);
        Assert.NotNull(run.Error);
    }

    private static WorkerIdentity IdentidadeDe(string id) => WorkerIdentity.FromEnvironment(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:Id"] = id })
            .Build());
}
