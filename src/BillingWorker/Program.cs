using BillingWorker;
using BillingWorker.Endpoints;
using BillingWorker.Persistence;
using Quartz;

var builder = WebApplication.CreateBuilder(args);

var identity = WorkerIdentity.FromEnvironment(builder.Configuration);
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Falta a connection string 'Postgres'.");

builder.Services.AddBillingServices(builder.Configuration);
builder.Services.AddBillingScheduler(builder.Configuration);
builder.Services.AddQuartzHostedService(options =>
{
    // Terminar o job em andamento antes de morrer evita deixar uma linha
    // RUNNING orfã em job_run a cada deploy.
    options.WaitForJobsToComplete = true;
    options.AwaitApplicationStarted = true;
});

var app = builder.Build();

// Antes do scheduler subir: as tabelas QRTZ_* precisam existir.
DatabaseMigrator.Run(connectionString);

app.MapGet("/health", () => Results.Ok(new { status = "UP", instance = identity.Id }));
app.MapJobEndpoints();

app.Logger.LogInformation("[{Instance}] worker no ar", identity.Id);

app.Run();

public partial class Program;
