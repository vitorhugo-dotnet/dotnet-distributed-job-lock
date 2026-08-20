using Dapper;
using Npgsql;

namespace BillingWorker.Billing;

/// <summary>
/// O trabalho de verdade do job. Fechar faturas e idempotente por construcao:
/// o filtro <c>status = 'PENDING'</c> vive dentro do mesmo UPDATE que muda o
/// status, entao nao existe janela entre ler e marcar. Se o job rodar duas
/// vezes — por bug, por retry ou por um cluster mal configurado — a segunda
/// execucao processa zero faturas em vez de cobrar o cliente de novo.
/// </summary>
public sealed class BillingCloseService(NpgsqlDataSource dataSource)
{
    public async Task<int> ClosePendingInvoicesAsync(string owner, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE invoices
               SET status    = 'CLOSED',
                   closed_at = now(),
                   closed_by = @owner
             WHERE status = 'PENDING'
            """,
            new { owner },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Repoe faturas PENDING para dar o que fazer ao job numa nova demonstracao.
    /// </summary>
    public async Task<int> SeedPendingInvoicesAsync(int count, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        return await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO invoices (customer, amount_cents)
            SELECT 'cliente-' || g, (g * 1000)::BIGINT
              FROM generate_series(1, @count) AS g
            """,
            new { count },
            cancellationToken: cancellationToken));
    }
}
