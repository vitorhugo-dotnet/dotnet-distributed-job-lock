using BillingWorker.Jobs;

namespace BillingWorker.Tests;

public class JobRunRepositoryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private JobRunRepository _repository = null!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        _repository = new JobRunRepository(postgres.DataSource);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RegistraInicioEConclusaoDaExecucao()
    {
        var id = await _repository.StartAsync("billing-close", "worker-1", default);
        await _repository.CompleteAsync(id, 25, default);

        var run = await _repository.FindLastAsync("billing-close", default);

        Assert.NotNull(run);
        Assert.Equal("worker-1", run!.Owner);
        Assert.Equal("SUCCESS", run.Status);
        Assert.Equal(25, run.ProcessedItems);
        Assert.NotNull(run.FinishedAt);
        Assert.Null(run.Error);
    }

    [Fact]
    public async Task RegistraFalhaComMensagem()
    {
        var id = await _repository.StartAsync("billing-close", "worker-1", default);
        await _repository.FailAsync(id, "banco caiu", default);

        var run = await _repository.FindLastAsync("billing-close", default);

        Assert.Equal("FAILED", run!.Status);
        Assert.Equal("banco caiu", run.Error);
    }

    [Fact]
    public async Task ExecucaoRecemIniciadaFicaComoRunning()
    {
        await _repository.StartAsync("billing-close", "worker-3", default);

        var run = await _repository.FindLastAsync("billing-close", default);

        Assert.Equal("RUNNING", run!.Status);
        Assert.Null(run.FinishedAt);
    }

    [Fact]
    public async Task DevolveSempreAExecucaoMaisRecente()
    {
        var antiga = await _repository.StartAsync("billing-close", "worker-1", default);
        await _repository.CompleteAsync(antiga, 25, default);
        var nova = await _repository.StartAsync("billing-close", "worker-2", default);
        await _repository.CompleteAsync(nova, 3, default);

        var run = await _repository.FindLastAsync("billing-close", default);

        Assert.Equal("worker-2", run!.Owner);
        Assert.Equal(3, run.ProcessedItems);
    }

    [Fact]
    public async Task SemExecucaoDevolveNulo()
    {
        Assert.Null(await _repository.FindLastAsync("inexistente", default));
    }
}
