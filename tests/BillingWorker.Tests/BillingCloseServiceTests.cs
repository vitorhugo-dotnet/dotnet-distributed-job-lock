using BillingWorker.Billing;
using Dapper;

namespace BillingWorker.Tests;

public class BillingCloseServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private BillingCloseService _service = null!;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        _service = new BillingCloseService(postgres.DataSource);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FechaTodasAsFaturasPendentes()
    {
        var processadas = await _service.ClosePendingInvoicesAsync("worker-1", default);

        Assert.Equal(25, processadas);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        var pendentes = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM invoices WHERE status = 'PENDING'");
        Assert.Equal(0, pendentes);
    }

    [Fact]
    public async Task SegundaExecucaoNaoProcessaNada()
    {
        await _service.ClosePendingInvoicesAsync("worker-1", default);

        var processadas = await _service.ClosePendingInvoicesAsync("worker-1", default);

        Assert.Equal(0, processadas);
    }

    [Fact]
    public async Task RegistraQuemFechouAFatura()
    {
        await _service.ClosePendingInvoicesAsync("worker-7", default);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        var owners = (await connection.QueryAsync<string>(
            "SELECT DISTINCT closed_by FROM invoices")).ToList();

        Assert.Equal(["worker-7"], owners);
    }

    [Fact]
    public async Task SeedCriaNovasFaturasPendentes()
    {
        await _service.ClosePendingInvoicesAsync("worker-1", default);

        var criadas = await _service.SeedPendingInvoicesAsync(10, default);

        Assert.Equal(10, criadas);
        Assert.Equal(10, await _service.ClosePendingInvoicesAsync("worker-1", default));
    }
}
