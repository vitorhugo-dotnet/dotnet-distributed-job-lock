namespace BillingWorker;

/// <summary>
/// Identidade da instancia. E o <c>InstanceId</c> do scheduler Quartz, o dono
/// gravado em <c>job_run</c> e o prefixo dos logs — as tres coisas precisam
/// concordar para que os logs expliquem quem ganhou o trigger.
/// </summary>
public sealed class WorkerIdentity
{
    private WorkerIdentity(string id) => Id = id;

    public string Id { get; }

    public static WorkerIdentity FromEnvironment(IConfiguration configuration)
    {
        var configured = configuration["Worker:Id"];
        return new WorkerIdentity(string.IsNullOrWhiteSpace(configured) ? Environment.MachineName : configured);
    }
}
