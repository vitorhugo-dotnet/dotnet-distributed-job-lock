using Dapper;
using Npgsql;

namespace BillingWorker.Jobs;

/// <summary>
/// Tabela de status propria. O job store do Quartz sabe quando o trigger
/// disparou, mas nao sabe o que o negocio fez — quantas faturas fecharam, em
/// qual instancia, com que resultado. Essa resposta mora aqui.
/// </summary>
public sealed class JobRunRepository(NpgsqlDataSource dataSource)
{
    public async Task<long> StartAsync(string jobName, string owner, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO job_run (job_name, owner, status, started_at)
            VALUES (@jobName, @owner, @status, now())
            RETURNING id
            """,
            new { jobName, owner, status = JobRunStatus.Running },
            cancellationToken: cancellationToken));
    }

    public async Task CompleteAsync(long id, int processedItems, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE job_run
               SET status = @status, processed_items = @processedItems, finished_at = now()
             WHERE id = @id
            """,
            new { id, processedItems, status = JobRunStatus.Success },
            cancellationToken: cancellationToken));
    }

    public async Task FailAsync(long id, string error, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE job_run
               SET status = @status, error = @error, finished_at = now()
             WHERE id = @id
            """,
            new { id, error, status = JobRunStatus.Failed },
            cancellationToken: cancellationToken));
    }

    public async Task<JobRun?> FindLastAsync(string jobName, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<JobRun>(new CommandDefinition(
            """
            SELECT id             AS Id,
                   job_name       AS JobName,
                   owner          AS Owner,
                   status         AS Status,
                   processed_items AS ProcessedItems,
                   started_at     AS StartedAt,
                   finished_at    AS FinishedAt,
                   error          AS Error
              FROM job_run
             WHERE job_name = @jobName
             ORDER BY started_at DESC, id DESC
             LIMIT 1
            """,
            new { jobName },
            cancellationToken: cancellationToken));
    }
}
