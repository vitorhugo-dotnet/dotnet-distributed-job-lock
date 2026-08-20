using BillingWorker.Billing;
using BillingWorker.Jobs;
using Npgsql;
using Quartz;

namespace BillingWorker;

/// <summary>
/// Um lugar so para a fiacao, para que o teste de cluster levante exatamente a
/// mesma configuracao que roda em producao. Se o clustering estivesse ligado
/// apenas no <c>Program.cs</c>, o teste provaria outra coisa.
/// </summary>
public static class SchedulerSetup
{
    public const string SchedulerName = "billing-scheduler";

    public static IServiceCollection AddBillingServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = ConnectionStringDe(configuration);

        services.AddSingleton(WorkerIdentity.FromEnvironment(configuration));
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<BillingCloseService>();
        services.AddSingleton<JobRunRepository>();

        return services;
    }

    public static IServiceCollection AddBillingScheduler(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = ConnectionStringDe(configuration);
        var identity = WorkerIdentity.FromEnvironment(configuration);
        var cron = configuration["Billing:Cron"] ?? "0 * * * * ?";

        services.AddQuartz(quartz =>
        {
            // Mesmo nome em todas as instancias: e isso que as coloca no mesmo
            // cluster. Id diferente em cada uma: e isso que permite o cluster
            // saber quem esta vivo e quem morreu no meio de um job.
            quartz.SchedulerName = SchedulerName;
            quartz.SchedulerId = identity.Id;

            quartz.UsePersistentStore(store =>
            {
                store.UseProperties = true;
                store.UseClustering(cluster =>
                {
                    cluster.CheckinInterval = TimeSpan.FromSeconds(10);
                    cluster.CheckinMisfireThreshold = TimeSpan.FromSeconds(20);
                });
                store.UsePostgres(postgres => postgres.ConnectionString = connectionString);
                store.UseSystemTextJsonSerializer();
            });

            quartz.AddJob<BillingCloseJob>(job => job
                .WithIdentity(BillingCloseJob.Key)
                .WithDescription("Fecha as faturas pendentes da janela.")
                .StoreDurably());

            quartz.AddTrigger(trigger => trigger
                .ForJob(BillingCloseJob.Key)
                .WithIdentity(BillingCloseJob.TriggerKey)
                // Se ninguem estava no ar na hora, a janela e pulada em vez de
                // acumular disparos atrasados quando o cluster voltar.
                .WithCronSchedule(cron, cronSchedule => cronSchedule.WithMisfireHandlingInstructionDoNothing()));
        });

        return services;
    }

    private static string ConnectionStringDe(IConfiguration configuration) =>
        configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Falta a connection string 'Postgres'.");
}
