using BillingWorker.Persistence;
using Dapper;
using Npgsql;
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

    private NpgsqlDataSource? _dataSource;

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource DataSource => _dataSource
        ?? throw new InvalidOperationException("Fixture ainda nao inicializado.");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DatabaseMigrator.Run(ConnectionString);
        _dataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    /// <summary>Volta ao estado pos-migracao: 25 faturas PENDING e nenhuma execucao.</summary>
    public async Task ResetAsync()
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            TRUNCATE invoices RESTART IDENTITY;
            TRUNCATE job_run RESTART IDENTITY;
            INSERT INTO invoices (customer, amount_cents)
            SELECT 'cliente-' || g, (g * 1000)::BIGINT FROM generate_series(1, 25) AS g;
            """);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}
