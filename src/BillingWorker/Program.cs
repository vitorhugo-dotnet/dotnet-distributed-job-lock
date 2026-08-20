using BillingWorker;

var builder = WebApplication.CreateBuilder(args);

var identity = WorkerIdentity.FromEnvironment(builder.Configuration);
builder.Services.AddSingleton(identity);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "UP", instance = identity.Id }));

app.Logger.LogInformation("[{Instance}] worker no ar", identity.Id);

app.Run();

public partial class Program;
