using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BillingWorker.Jobs;
using Dapper;

namespace BillingWorker.Tests;

public class JobEndpointsTests(WorkerAppFixture fixture) : IClassFixture<WorkerAppFixture>, IAsyncLifetime
{
    private JobRunRepository _runs = null!;

    public async Task InitializeAsync()
    {
        await fixture.Postgres.ResetAsync();
        _runs = new JobRunRepository(fixture.Postgres.DataSource);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task StatusSemExecucaoDevolveNeverRun()
    {
        var status = await fixture.Client.GetFromJsonAsync<JsonElement>("/jobs/billing-close/status");

        Assert.Equal("billing-close", status.GetProperty("job").GetString());
        Assert.Equal("NEVER_RUN", status.GetProperty("lastStatus").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastRunAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastOwner").ValueKind);
        Assert.Equal(0, status.GetProperty("processedItems").GetInt32());
    }

    [Fact]
    public async Task StatusDevolveUltimaExecucao()
    {
        var id = await _runs.StartAsync(BillingCloseJob.JobName, "worker-2", default);
        await _runs.CompleteAsync(id, 42, default);

        var status = await fixture.Client.GetFromJsonAsync<JsonElement>("/jobs/billing-close/status");

        Assert.Equal("worker-2", status.GetProperty("lastOwner").GetString());
        Assert.Equal("SUCCESS", status.GetProperty("lastStatus").GetString());
        Assert.Equal(42, status.GetProperty("processedItems").GetInt32());
        Assert.EndsWith("Z", status.GetProperty("lastRunAt").GetString());
    }

    [Fact]
    public async Task RunNowDisparaOJobEFechaAsFaturas()
    {
        var resposta = await fixture.Client.PostAsync("/jobs/billing-close/run-now", null);

        Assert.Equal(HttpStatusCode.Accepted, resposta.StatusCode);

        await EsperarExecucaoAsync();

        var status = await fixture.Client.GetFromJsonAsync<JsonElement>("/jobs/billing-close/status");
        Assert.Equal("SUCCESS", status.GetProperty("lastStatus").GetString());
        Assert.Equal(25, status.GetProperty("processedItems").GetInt32());
        Assert.Equal("worker-teste", status.GetProperty("lastOwner").GetString());
    }

    [Fact]
    public async Task SeedRepoeFaturasPendentes()
    {
        var resposta = await fixture.Client.PostAsync("/invoices/seed?count=7", null);

        Assert.Equal(HttpStatusCode.Accepted, resposta.StatusCode);

        await using var connection = await fixture.Postgres.DataSource.OpenConnectionAsync();
        var pendentes = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM invoices WHERE status = 'PENDING'");

        Assert.Equal(32, pendentes);
    }

    [Fact]
    public async Task HealthIdentificaAInstancia()
    {
        var health = await fixture.Client.GetFromJsonAsync<JsonElement>("/health");

        Assert.Equal("UP", health.GetProperty("status").GetString());
        Assert.Equal("worker-teste", health.GetProperty("instance").GetString());
    }

    private async Task EsperarExecucaoAsync()
    {
        var limite = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < limite)
        {
            var run = await _runs.FindLastAsync(BillingCloseJob.JobName, default);

            if (run?.FinishedAt is not null)
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail("O job disparado por run-now nao concluiu em 30s.");
    }
}
