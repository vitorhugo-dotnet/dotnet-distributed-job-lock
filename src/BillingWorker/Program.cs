using BillingWorker;
using BillingWorker.Persistence;

var builder = WebApplication.CreateBuilder(args);

var identity = WorkerIdentity.FromEnvironment(builder.Configuration);
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Falta a connection string 'Postgres'.");

builder.Services.AddSingleton(identity);

var app = builder.Build();

DatabaseMigrator.Run(connectionString);

app.MapGet("/health", () => Results.Ok(new { status = "UP", instance = identity.Id }));

app.Logger.LogInformation("[{Instance}] worker no ar", identity.Id);

app.Run();

public partial class Program;
