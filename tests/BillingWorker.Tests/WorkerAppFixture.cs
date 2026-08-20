using Microsoft.AspNetCore.Mvc.Testing;

namespace BillingWorker.Tests;

/// <summary>
/// Sobe o worker inteiro — migracao, scheduler e endpoints — contra o
/// PostgreSQL do container. Uma unica vez por classe de teste: o Quartz guarda
/// o log provider num campo estatico do processo, entao levantar e derrubar o
/// host a cada teste deixaria os seguintes com um provider descartado.
/// </summary>
public sealed class WorkerAppFixture : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _app;

    public PostgresFixture Postgres { get; } = new();

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();

        // O Program le a connection string no topo, antes de o host existir —
        // entao ConfigureAppConfiguration chegaria tarde demais. Variavel de
        // ambiente e o mesmo caminho que o docker-compose usa em producao.
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", Postgres.ConnectionString);
        Environment.SetEnvironmentVariable("Worker__Id", "worker-teste");
        // Cron distante: o unico gatilho durante os testes e o run-now.
        Environment.SetEnvironmentVariable("Billing__Cron", "0 0 5 * * ?");

        _app = new WebApplicationFactory<Program>();
        Client = _app.CreateClient();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", null);
        Environment.SetEnvironmentVariable("Worker__Id", null);
        Environment.SetEnvironmentVariable("Billing__Cron", null);

        await Postgres.DisposeAsync();
    }
}
