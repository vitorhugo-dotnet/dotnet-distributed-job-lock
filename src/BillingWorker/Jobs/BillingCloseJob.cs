using BillingWorker.Billing;
using Quartz;

namespace BillingWorker.Jobs;

/// <summary>
/// O job que fecha o faturamento. <see cref="DisallowConcurrentExecutionAttribute"/>
/// vale para o cluster inteiro quando o job store e persistente: duas instancias
/// nao executam o mesmo <c>JobDetail</c> ao mesmo tempo, mesmo em processos e
/// maquinas diferentes.
/// </summary>
[DisallowConcurrentExecution]
public sealed class BillingCloseJob(
    BillingCloseService billing,
    JobRunRepository runs,
    WorkerIdentity identity,
    ILogger<BillingCloseJob> logger) : IJob
{
    public const string JobName = "billing-close";

    public static readonly JobKey Key = new(JobName, "billing");
    public static readonly TriggerKey TriggerKey = new("billing-close-trigger", "billing");

    public Task Execute(IJobExecutionContext context) => RunAsync(context.CancellationToken);

    /// <summary>
    /// O miolo, separado de <see cref="Execute"/> para ser testavel sem fabricar
    /// um <see cref="IJobExecutionContext"/>.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var runId = await runs.StartAsync(JobName, identity.Id, cancellationToken);
        logger.LogInformation("[{Instance}] ganhou o trigger de {Job} (run {RunId})", identity.Id, JobName, runId);

        try
        {
            var processed = await billing.ClosePendingInvoicesAsync(identity.Id, cancellationToken);
            await runs.CompleteAsync(runId, processed, cancellationToken);
            logger.LogInformation("[{Instance}] {Job} fechou {Processed} faturas", identity.Id, JobName, processed);
        }
        catch (Exception ex)
        {
            // CancellationToken.None de proposito: registrar a falha e a ultima
            // coisa util que da para fazer quando o cancelamento ja disparou.
            await runs.FailAsync(runId, ex.Message, CancellationToken.None);
            logger.LogError(ex, "[{Instance}] {Job} falhou", identity.Id, JobName);
            throw;
        }
    }
}
