using Testcontainers.PostgreSql;

namespace BillingWorker.Tests;

/// <summary>
/// Um PostgreSQL de verdade por classe de teste. O job store clustered do
/// Quartz depende de lock de linha no banco — testar isso contra um fake em
/// memoria nao provaria nada.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("billing")
        .WithUsername("billing")
        .WithPassword("billing")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
