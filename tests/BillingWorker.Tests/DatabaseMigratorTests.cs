using BillingWorker.Persistence;
using Dapper;
using Npgsql;

namespace BillingWorker.Tests;

public class DatabaseMigratorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task CriaTabelasDoQuartzEDoDominio()
    {
        DatabaseMigrator.Run(postgres.ConnectionString);

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        var tabelas = (await connection.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'")).ToList();

        Assert.Contains("qrtz_locks", tabelas);
        Assert.Contains("qrtz_triggers", tabelas);
        Assert.Contains("qrtz_scheduler_state", tabelas);
        Assert.Contains("invoices", tabelas);
        Assert.Contains("job_run", tabelas);
    }

    [Fact]
    public async Task RodarDuasVezesNaoQuebraNemDuplicaSeed()
    {
        DatabaseMigrator.Run(postgres.ConnectionString);
        DatabaseMigrator.Run(postgres.ConnectionString);

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        var faturas = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM invoices");

        Assert.Equal(25, faturas);
    }
}
