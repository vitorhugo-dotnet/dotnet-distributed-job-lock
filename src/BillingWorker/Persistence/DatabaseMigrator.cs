using Dapper;
using DbUp;
using Npgsql;

namespace BillingWorker.Persistence;

/// <summary>
/// Roda as migracoes no boot. Como as tres instancias sobem juntas, a migracao
/// e serializada por um advisory lock do proprio PostgreSQL: a primeira migra,
/// as outras esperam e encontram tudo pronto.
/// </summary>
public static class DatabaseMigrator
{
    private const long AdvisoryLockKey = 4242;

    public static void Run(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        connection.Execute("SELECT pg_advisory_lock(@key)", new { key = AdvisoryLockKey });

        try
        {
            var result = DeployChanges.To
                .PostgresqlDatabase(connectionString)
                .WithScriptsEmbeddedInAssembly(typeof(DatabaseMigrator).Assembly)
                .WithTransactionPerScript()
                .LogToNowhere()
                .Build()
                .PerformUpgrade();

            if (!result.Successful)
            {
                throw new InvalidOperationException("Falha ao migrar o banco.", result.Error);
            }
        }
        finally
        {
            connection.Execute("SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLockKey });
        }
    }
}
