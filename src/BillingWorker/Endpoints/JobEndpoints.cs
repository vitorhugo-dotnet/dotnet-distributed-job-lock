using BillingWorker.Billing;
using BillingWorker.Jobs;
using Quartz;

namespace BillingWorker.Endpoints;

/// <summary>Resposta de <c>GET /jobs/billing-close/status</c>.</summary>
public sealed record JobStatusResponse(
    string Job,
    DateTime? LastRunAt,
    string? LastOwner,
    string LastStatus,
    int ProcessedItems);

public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        app.MapGet("/jobs/billing-close/status", async (JobRunRepository runs, CancellationToken cancellationToken) =>
        {
            var run = await runs.FindLastAsync(BillingCloseJob.JobName, cancellationToken);

            return Results.Ok(run is null
                ? new JobStatusResponse(BillingCloseJob.JobName, null, null, JobRunStatus.NeverRun, 0)
                : new JobStatusResponse(
                    BillingCloseJob.JobName,
                    run.StartedAt,
                    run.Owner,
                    run.Status,
                    run.ProcessedItems));
        });

        // Dispara o trigger no job store compartilhado. Quem recebeu o POST nao
        // e necessariamente quem vai executar — e essa e a demonstracao mais
        // curta de que a coordenacao esta no banco, nao no processo.
        app.MapPost("/jobs/billing-close/run-now", async (
            ISchedulerFactory schedulerFactory,
            WorkerIdentity identity,
            CancellationToken cancellationToken) =>
        {
            var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
            await scheduler.TriggerJob(BillingCloseJob.Key, cancellationToken);

            return Results.Accepted(value: new { job = BillingCloseJob.JobName, requestedBy = identity.Id });
        });

        app.MapPost("/invoices/seed", async (
            BillingCloseService billing,
            CancellationToken cancellationToken,
            int count = 25) =>
        {
            var created = await billing.SeedPendingInvoicesAsync(count, cancellationToken);

            return Results.Accepted(value: new { created });
        });
    }
}
