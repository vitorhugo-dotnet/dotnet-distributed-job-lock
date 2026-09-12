# distributed-job-lock (.NET) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Um worker .NET 10 que executa o job `billing-close` exatamente uma vez por janela mesmo com tres instancias no ar, coordenado pelo job store clustered do Quartz.NET sobre PostgreSQL.

**Architecture:** Um unico host ASP.NET Core roda o `IScheduler` do Quartz (hosted service) e expoe os endpoints minimal API. Todas as instancias compartilham `InstanceName = billing-scheduler` e um `InstanceId` unico por container; o job store no PostgreSQL decide qual instancia adquire o trigger. O job fecha faturas PENDING com um unico UPDATE atomico e audita a execucao na tabela `job_run`.

**Tech Stack:** .NET 10, Quartz.NET 3.19.1 (`Quartz.AspNetCore`, `Quartz.Serialization.Json`), Npgsql 10.0.3, Dapper 2.1.79, DbUp 7.0.1, xUnit 2.9.3, Testcontainers 4.14.0, Docker Compose.

**Spec:** `docs/superpowers/specs/2026-08-20-distributed-job-lock-design.md`

## Global Constraints

- `TargetFramework` = `net10.0`, `Nullable` e `ImplicitUsings` habilitados em todos os projetos.
- Estrutura de pastas segue os projetos irmaos: `src/<Projeto>/`, `tests/<Projeto>.Tests/`, arquivo de solucao `.slnx` na raiz.
- Versoes exatas: Quartz 3.19.1, Npgsql 10.0.3, Dapper 2.1.79, dbup-postgresql 7.0.1, Testcontainers.PostgreSql 4.14.0, xunit 2.9.3, xunit.runner.visualstudio 3.1.4, Microsoft.NET.Test.Sdk 17.14.1, Microsoft.AspNetCore.Mvc.Testing 10.0.11.
- Nome do job: `billing-close`. Nome do scheduler: `billing-scheduler`.
- Testes de integracao usam PostgreSQL real via Testcontainers — nada de fake in-memory para o job store.
- `docs/` e artefatos do superpowers NAO entram em git e NAO entram no `.gitignore`.
- README em pt-BR.
- Commits em pt-BR, prefixo convencional (`chore:`, `feat:`, `docs:`), um por task concluida.

---

### Task 1: Scaffold da solucao

**Files:**
- Create: `DistributedJobLock.slnx`
- Create: `src/BillingWorker/BillingWorker.csproj`
- Create: `src/BillingWorker/Program.cs`
- Create: `src/BillingWorker/WorkerIdentity.cs`
- Create: `src/BillingWorker/appsettings.json`
- Create: `.gitignore`
- Test: `tests/BillingWorker.Tests/BillingWorker.Tests.csproj`, `tests/BillingWorker.Tests/WorkerIdentityTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces: `sealed class WorkerIdentity { string Id { get; } }` com `static WorkerIdentity FromEnvironment(IConfiguration configuration)` — le `Worker:Id` (env `Worker__Id`) e cai para `Environment.MachineName` quando vazio.

- [ ] **Step 1: Criar os projetos e a solucao**

```bash
dotnet new web -o src/BillingWorker -n BillingWorker
dotnet new xunit -o tests/BillingWorker.Tests -n BillingWorker.Tests
dotnet new sln -n DistributedJobLock --format slnx
dotnet sln add src/BillingWorker/BillingWorker.csproj tests/BillingWorker.Tests/BillingWorker.Tests.csproj
dotnet add tests/BillingWorker.Tests reference src/BillingWorker
```

- [ ] **Step 2: Escrever o teste que falha**

```csharp
public class WorkerIdentityTests
{
    [Fact]
    public void UsaWorkerIdDaConfiguracaoQuandoDefinido()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:Id"] = "worker-2" })
            .Build();

        Assert.Equal("worker-2", WorkerIdentity.FromEnvironment(config).Id);
    }

    [Fact]
    public void CaiParaOHostnameQuandoNaoConfigurado()
    {
        var config = new ConfigurationBuilder().Build();

        Assert.Equal(Environment.MachineName, WorkerIdentity.FromEnvironment(config).Id);
    }
}
```

- [ ] **Step 3: Rodar e ver falhar**

Run: `dotnet test`
Expected: erro de compilacao — `WorkerIdentity` nao existe.

- [ ] **Step 4: Implementar**

```csharp
namespace BillingWorker;

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
```

`Program.cs` minimo: registra `WorkerIdentity` como singleton, mapeia `GET /health` devolvendo `Results.Ok(new { status = "UP", instance = identity.Id })`, e declara `public partial class Program;` no fim para o `WebApplicationFactory`.

- [ ] **Step 5: Rodar os testes**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: scaffold do worker .net 10 com identidade de instancia"
```

---

### Task 2: Migracoes com advisory lock

**Files:**
- Create: `src/BillingWorker/Persistence/DatabaseMigrator.cs`
- Create: `src/BillingWorker/Persistence/Scripts/0001_quartz_tables.sql`
- Create: `src/BillingWorker/Persistence/Scripts/0002_billing_tables.sql`
- Modify: `src/BillingWorker/BillingWorker.csproj` (EmbeddedResource dos scripts + pacotes Npgsql/Dapper/dbup-postgresql)
- Modify: `src/BillingWorker/Program.cs`
- Test: `tests/BillingWorker.Tests/PostgresFixture.cs`, `tests/BillingWorker.Tests/DatabaseMigratorTests.cs`

**Interfaces:**
- Consumes: nada da Task 1.
- Produces: `static class DatabaseMigrator { static void Run(string connectionString); }` — idempotente, serializado por `pg_advisory_lock(4242)`.
- Produces: `PostgresFixture : IAsyncLifetime` com `string ConnectionString { get; }` (container `postgres:16-alpine`), usado pelas tasks seguintes.

- [ ] **Step 1: Escrever o teste que falha**

```csharp
public class DatabaseMigratorTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public DatabaseMigratorTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task CriaTabelasDoQuartzEDoDominio()
    {
        DatabaseMigrator.Run(_postgres.ConnectionString);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        var tabelas = (await connection.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'")).ToList();

        Assert.Contains("qrtz_locks", tabelas);
        Assert.Contains("qrtz_triggers", tabelas);
        Assert.Contains("invoices", tabelas);
        Assert.Contains("job_run", tabelas);
    }

    [Fact]
    public void RodarDuasVezesNaoQuebra()
    {
        DatabaseMigrator.Run(_postgres.ConnectionString);
        DatabaseMigrator.Run(_postgres.ConnectionString);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test --filter DatabaseMigratorTests`
Expected: erro de compilacao — `DatabaseMigrator` nao existe.

- [ ] **Step 3: Implementar os scripts**

`0001_quartz_tables.sql` e o schema oficial `tables_postgres.sql` do Quartz.NET **sem o bloco inicial que dropa as tabelas** (DbUp roda uma vez; dropar seria destrutivo).

`0002_billing_tables.sql`:

```sql
CREATE TABLE invoices (
    id           BIGSERIAL PRIMARY KEY,
    customer     TEXT        NOT NULL,
    amount_cents BIGINT      NOT NULL,
    status       TEXT        NOT NULL DEFAULT 'PENDING',
    closed_at    TIMESTAMPTZ NULL,
    closed_by    TEXT        NULL
);

CREATE INDEX idx_invoices_status ON invoices (status);

CREATE TABLE job_run (
    id              BIGSERIAL PRIMARY KEY,
    job_name        TEXT        NOT NULL,
    owner           TEXT        NOT NULL,
    status          TEXT        NOT NULL,
    processed_items INT         NOT NULL DEFAULT 0,
    started_at      TIMESTAMPTZ NOT NULL,
    finished_at     TIMESTAMPTZ NULL,
    error           TEXT        NULL
);

CREATE INDEX idx_job_run_job_started ON job_run (job_name, started_at DESC);

INSERT INTO invoices (customer, amount_cents)
SELECT 'cliente-' || g, (g * 1000)::BIGINT FROM generate_series(1, 25) AS g;
```

- [ ] **Step 4: Implementar o migrator**

```csharp
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
```

No `.csproj`: `<EmbeddedResource Include="Persistence\Scripts\*.sql" />`.

`Program.cs` chama `DatabaseMigrator.Run(connectionString)` antes de `app.Run()`, lendo `ConnectionStrings:Postgres`.

- [ ] **Step 5: Rodar os testes**

Run: `dotnet test`
Expected: PASS (o Docker precisa estar no ar para o Testcontainers).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: migrar schema do quartz e do dominio com advisory lock"
```

---

### Task 3: Fechamento idempotente de faturas

**Files:**
- Create: `src/BillingWorker/Billing/BillingCloseService.cs`
- Test: `tests/BillingWorker.Tests/BillingCloseServiceTests.cs`

**Interfaces:**
- Consumes: `PostgresFixture`, `DatabaseMigrator.Run`.
- Produces: `sealed class BillingCloseService(NpgsqlDataSource dataSource)` com
  `Task<int> ClosePendingInvoicesAsync(string owner, CancellationToken ct)` e
  `Task<int> SeedPendingInvoicesAsync(int count, CancellationToken ct)`.

- [ ] **Step 1: Escrever os testes que falham**

```csharp
[Fact]
public async Task FechaTodasAsFaturasPendentes()
{
    var processadas = await _service.ClosePendingInvoicesAsync("worker-1", default);

    Assert.Equal(25, processadas);

    await using var connection = await _dataSource.OpenConnectionAsync();
    var pendentes = await connection.ExecuteScalarAsync<int>(
        "SELECT count(*) FROM invoices WHERE status = 'PENDING'");
    Assert.Equal(0, pendentes);
}

[Fact]
public async Task SegundaExecucaoNaoProcessaNada()
{
    await _service.ClosePendingInvoicesAsync("worker-1", default);

    var processadas = await _service.ClosePendingInvoicesAsync("worker-1", default);

    Assert.Equal(0, processadas);
}

[Fact]
public async Task RegistraQuemFechouAFatura()
{
    await _service.ClosePendingInvoicesAsync("worker-7", default);

    await using var connection = await _dataSource.OpenConnectionAsync();
    var owners = (await connection.QueryAsync<string>(
        "SELECT DISTINCT closed_by FROM invoices")).ToList();
    Assert.Equal(["worker-7"], owners);
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test --filter BillingCloseServiceTests`
Expected: erro de compilacao — `BillingCloseService` nao existe.

- [ ] **Step 3: Implementar**

```csharp
public sealed class BillingCloseService(NpgsqlDataSource dataSource)
{
    public async Task<int> ClosePendingInvoicesAsync(string owner, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE invoices
               SET status = 'CLOSED', closed_at = now(), closed_by = @owner
             WHERE status = 'PENDING'
            """,
            new { owner },
            cancellationToken: cancellationToken));
    }
}
```

`SeedPendingInvoicesAsync` faz `INSERT INTO invoices (customer, amount_cents) SELECT 'cliente-' || g, g * 1000 FROM generate_series(1, @count) AS g` e devolve o numero de linhas inseridas.

- [ ] **Step 4: Rodar os testes**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: fechar faturas pendentes de forma idempotente"
```

---

### Task 4: Auditoria de execucao e o job

**Files:**
- Create: `src/BillingWorker/Jobs/JobRun.cs`
- Create: `src/BillingWorker/Jobs/JobRunRepository.cs`
- Create: `src/BillingWorker/Jobs/BillingCloseJob.cs`
- Test: `tests/BillingWorker.Tests/JobRunRepositoryTests.cs`, `tests/BillingWorker.Tests/BillingCloseJobTests.cs`

**Interfaces:**
- Consumes: `BillingCloseService`, `WorkerIdentity`, `PostgresFixture`.
- Produces:
  - `sealed record JobRun(long Id, string JobName, string Owner, string Status, int ProcessedItems, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Error)`
  - `sealed class JobRunRepository(NpgsqlDataSource dataSource)` com
    `Task<long> StartAsync(string jobName, string owner, CancellationToken ct)`,
    `Task CompleteAsync(long id, int processedItems, CancellationToken ct)`,
    `Task FailAsync(long id, string error, CancellationToken ct)`,
    `Task<JobRun?> FindLastAsync(string jobName, CancellationToken ct)`.
  - `const string BillingCloseJob.JobName = "billing-close";`
  - `static readonly JobKey BillingCloseJob.Key = new("billing-close", "billing");`
  - `Task BillingCloseJob.RunAsync(CancellationToken ct)`.

- [ ] **Step 1: Escrever os testes que falham**

```csharp
[Fact]
public async Task RegistraInicioEConclusaoDaExecucao()
{
    var id = await _repository.StartAsync("billing-close", "worker-1", default);
    await _repository.CompleteAsync(id, 25, default);

    var run = await _repository.FindLastAsync("billing-close", default);

    Assert.NotNull(run);
    Assert.Equal("worker-1", run!.Owner);
    Assert.Equal("SUCCESS", run.Status);
    Assert.Equal(25, run.ProcessedItems);
    Assert.NotNull(run.FinishedAt);
}

[Fact]
public async Task RegistraFalhaComMensagem()
{
    var id = await _repository.StartAsync("billing-close", "worker-1", default);
    await _repository.FailAsync(id, "banco caiu", default);

    var run = await _repository.FindLastAsync("billing-close", default);

    Assert.Equal("FAILED", run!.Status);
    Assert.Equal("banco caiu", run.Error);
}

[Fact]
public async Task SemExecucaoDevolveNulo()
{
    Assert.Null(await _repository.FindLastAsync("inexistente", default));
}
```

E para o job:

```csharp
[Fact]
public async Task JobFechaFaturasEAuditaExecucao()
{
    await _job.RunAsync(CancellationToken.None);

    var run = await _repository.FindLastAsync(BillingCloseJob.JobName, default);
    Assert.Equal("SUCCESS", run!.Status);
    Assert.Equal(25, run.ProcessedItems);
    Assert.Equal("worker-teste", run.Owner);
}

[Fact]
public async Task JobRegistraFalhaQuandoOFechamentoQuebra()
{
    var jobQuebrado = CriarJobComBancoInvalido();

    await Assert.ThrowsAnyAsync<Exception>(() => jobQuebrado.RunAsync(CancellationToken.None));
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test --filter "JobRunRepositoryTests|BillingCloseJobTests"`
Expected: erro de compilacao.

- [ ] **Step 3: Implementar**

`BillingCloseJob` implementa `IJob` com `[DisallowConcurrentExecution]`; `Execute(IJobExecutionContext)` apenas delega para `RunAsync(context.CancellationToken)`, o que mantem o miolo testavel sem fabricar um `IJobExecutionContext`:

```csharp
[DisallowConcurrentExecution]
public sealed class BillingCloseJob(
    BillingCloseService billing,
    JobRunRepository runs,
    WorkerIdentity identity,
    ILogger<BillingCloseJob> logger) : IJob
{
    public const string JobName = "billing-close";
    public static readonly JobKey Key = new(JobName, "billing");

    public Task Execute(IJobExecutionContext context) => RunAsync(context.CancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var runId = await runs.StartAsync(JobName, identity.Id, cancellationToken);
        logger.LogInformation("[{Instance}] iniciando {Job} (run {RunId})", identity.Id, JobName, runId);
        try
        {
            var processed = await billing.ClosePendingInvoicesAsync(identity.Id, cancellationToken);
            await runs.CompleteAsync(runId, processed, cancellationToken);
            logger.LogInformation("[{Instance}] {Job} fechou {Processed} faturas", identity.Id, JobName, processed);
        }
        catch (Exception ex)
        {
            await runs.FailAsync(runId, ex.Message, CancellationToken.None);
            logger.LogError(ex, "[{Instance}] {Job} falhou", identity.Id, JobName);
            throw;
        }
    }
}
```

- [ ] **Step 4: Rodar os testes**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: auditar execucao do job billing-close com owner e status"
```

---

### Task 5: Quartz clustered e prevencao de execucao duplicada

**Files:**
- Create: `src/BillingWorker/SchedulerSetup.cs`
- Modify: `src/BillingWorker/Program.cs`
- Modify: `src/BillingWorker/appsettings.json`
- Test: `tests/BillingWorker.Tests/ClusteredSchedulerTests.cs`

**Interfaces:**
- Consumes: `BillingCloseJob.Key`, `DatabaseMigrator.Run`.
- Produces: `static class SchedulerSetup { static IServiceCollection AddBillingScheduler(this IServiceCollection services, IConfiguration configuration); }`, que registra Quartz com job store ADO.NET, clustering e serializer JSON, e o hosted service com `WaitForJobsToComplete = true`.

- [ ] **Step 1: Escrever o teste que falha**

O teste levanta **tres schedulers clustered** contra o mesmo banco, agenda um unico trigger e verifica que so uma execucao aconteceu:

```csharp
[Fact]
public async Task ApenasUmaInstanciaExecutaOTrigger()
{
    await using var instanciaA = await CriarInstanciaAsync("worker-a");
    await using var instanciaB = await CriarInstanciaAsync("worker-b");
    await using var instanciaC = await CriarInstanciaAsync("worker-c");

    await instanciaA.Scheduler.ScheduleJob(
        JobBuilder.Create<BillingCloseJob>().WithIdentity(BillingCloseJob.Key).Build(),
        TriggerBuilder.Create()
            .WithIdentity("billing-close-trigger", "billing")
            .StartAt(DateTimeOffset.UtcNow.AddSeconds(2))
            .Build());

    await EsperarExecucaoAsync(TimeSpan.FromSeconds(60));

    await using var connection = await _dataSource.OpenConnectionAsync();
    var execucoes = await connection.ExecuteScalarAsync<int>(
        "SELECT count(*) FROM job_run WHERE job_name = 'billing-close'");
    var faturasFechadas = await connection.ExecuteScalarAsync<int>(
        "SELECT count(*) FROM invoices WHERE status = 'CLOSED'");

    Assert.Equal(1, execucoes);
    Assert.Equal(25, faturasFechadas);
}
```

`CriarInstanciaAsync(id)` monta um `ServiceProvider` com `WorkerIdentity` fixo nesse id, `NpgsqlDataSource` do container, os repositorios, e Quartz configurado via `services.AddQuartz(...)` com `SchedulerName = "billing-scheduler"`, `SchedulerId = id`, `UseClustering`, `UsePostgres` e serializer JSON; obtem o `IScheduler` por `ISchedulerFactory` e chama `Start()`.

`EsperarExecucaoAsync` faz polling em `job_run` ate existir pelo menos uma linha com `finished_at IS NOT NULL` ou estourar o timeout; depois espera uma folga fixa para dar chance de uma segunda execucao indevida aparecer.

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test --filter ClusteredSchedulerTests`
Expected: erro de compilacao / falha por ausencia da configuracao clustered.

- [ ] **Step 3: Implementar a configuracao**

```csharp
public static IServiceCollection AddBillingScheduler(this IServiceCollection services, IConfiguration configuration)
{
    var connectionString = configuration.GetConnectionString("Postgres")!;
    var identity = WorkerIdentity.FromEnvironment(configuration);
    var cron = configuration["Billing:Cron"] ?? "0 * * * * ?";

    services.AddQuartz(q =>
    {
        q.SchedulerName = "billing-scheduler";
        q.SchedulerId = identity.Id;

        q.UsePersistentStore(store =>
        {
            store.UseProperties = true;
            store.UseClustering(cluster => cluster.CheckinInterval = TimeSpan.FromSeconds(10));
            store.UsePostgres(postgres => postgres.ConnectionString = connectionString);
            store.UseSystemTextJsonSerializer();
        });

        q.AddJob<BillingCloseJob>(job => job.WithIdentity(BillingCloseJob.Key).StoreDurably());
        q.AddTrigger(trigger => trigger
            .ForJob(BillingCloseJob.Key)
            .WithIdentity("billing-close-trigger", "billing")
            .WithCronSchedule(cron, cronSchedule => cronSchedule.WithMisfireHandlingInstructionDoNothing()));
    });

    services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
    return services;
}
```

Se `UseSystemTextJsonSerializer` nao existir na 3.19.1, usar `UseNewtonsoftJsonSerializer` (pacote `Quartz.Serialization.Json`) — confirmar pelo compilador, nao pela memoria.

- [ ] **Step 4: Rodar os testes**

Run: `dotnet test`
Expected: PASS — exatamente uma execucao registrada.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: coordenar o job com quartz clustered no postgres"
```

---

### Task 6: Endpoints HTTP

**Files:**
- Create: `src/BillingWorker/Endpoints/JobEndpoints.cs`
- Modify: `src/BillingWorker/Program.cs`
- Create: `api.http`
- Test: `tests/BillingWorker.Tests/JobEndpointsTests.cs`

**Interfaces:**
- Consumes: `JobRunRepository.FindLastAsync`, `BillingCloseService.SeedPendingInvoicesAsync`, `ISchedulerFactory`.
- Produces: `static class JobEndpoints { static void MapJobEndpoints(this WebApplication app); }` e o contrato
  `sealed record JobStatusResponse(string Job, DateTimeOffset? LastRunAt, string? LastOwner, string LastStatus, int ProcessedItems)`
  serializado em camelCase.

- [ ] **Step 1: Escrever os testes que falham**

```csharp
[Fact]
public async Task StatusSemExecucaoDevolveNeverRun()
{
    var response = await _client.GetFromJsonAsync<JsonElement>("/jobs/billing-close/status");

    Assert.Equal("billing-close", response.GetProperty("job").GetString());
    Assert.Equal("NEVER_RUN", response.GetProperty("lastStatus").GetString());
    Assert.Equal(JsonValueKind.Null, response.GetProperty("lastRunAt").ValueKind);
}

[Fact]
public async Task StatusDevolveUltimaExecucao()
{
    var id = await _repository.StartAsync("billing-close", "worker-2", default);
    await _repository.CompleteAsync(id, 42, default);

    var response = await _client.GetFromJsonAsync<JsonElement>("/jobs/billing-close/status");

    Assert.Equal("worker-2", response.GetProperty("lastOwner").GetString());
    Assert.Equal("SUCCESS", response.GetProperty("lastStatus").GetString());
    Assert.Equal(42, response.GetProperty("processedItems").GetInt32());
}

[Fact]
public async Task RunNowDisparaOJobEFechaAsFaturas()
{
    var response = await _client.PostAsync("/jobs/billing-close/run-now", null);
    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

    await EsperarExecucaoAsync(TimeSpan.FromSeconds(30));

    var status = await _client.GetFromJsonAsync<JsonElement>("/jobs/billing-close/status");
    Assert.Equal("SUCCESS", status.GetProperty("lastStatus").GetString());
    Assert.Equal(25, status.GetProperty("processedItems").GetInt32());
}
```

A fabrica de teste e um `WebApplicationFactory<Program>` que sobrescreve `ConnectionStrings:Postgres` para o container, define `Worker:Id` e usa um cron que nao dispara sozinho durante o teste (`0 0 5 * * ?`), para que o unico gatilho seja o `run-now`.

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test --filter JobEndpointsTests`
Expected: 404 nas rotas.

- [ ] **Step 3: Implementar**

```csharp
public static void MapJobEndpoints(this WebApplication app)
{
    app.MapGet("/jobs/billing-close/status", async (JobRunRepository runs, CancellationToken ct) =>
    {
        var run = await runs.FindLastAsync(BillingCloseJob.JobName, ct);
        return Results.Ok(run is null
            ? new JobStatusResponse(BillingCloseJob.JobName, null, null, "NEVER_RUN", 0)
            : new JobStatusResponse(BillingCloseJob.JobName, run.StartedAt, run.Owner, run.Status, run.ProcessedItems));
    });

    app.MapPost("/jobs/billing-close/run-now", async (ISchedulerFactory factory, WorkerIdentity identity, CancellationToken ct) =>
    {
        var scheduler = await factory.GetScheduler(ct);
        await scheduler.TriggerJob(BillingCloseJob.Key, ct);
        return Results.Accepted(value: new { job = BillingCloseJob.JobName, requestedBy = identity.Id });
    });

    app.MapPost("/invoices/seed", async (BillingCloseService billing, CancellationToken ct, int count = 25) =>
        Results.Accepted(value: new { created = await billing.SeedPendingInvoicesAsync(count, ct) }));
}
```

- [ ] **Step 4: Rodar os testes**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Escrever o `api.http`**

Requisicoes para os quatro endpoints, no mesmo estilo dos projetos irmaos.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: expor status e disparo manual do job billing-close"
```

---

### Task 7: Docker e scale

**Files:**
- Create: `Dockerfile`
- Create: `.dockerignore`
- Create: `docker-compose.yml`

**Interfaces:**
- Consumes: `GET /health` (healthcheck), variavel `ConnectionStrings__Postgres`.
- Produces: servico `worker` escalavel via `docker compose up --scale worker=3`.

- [ ] **Step 1: Escrever o Dockerfile multi-stage**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/BillingWorker/BillingWorker.csproj src/BillingWorker/
RUN dotnet restore src/BillingWorker/BillingWorker.csproj
COPY src/ src/
RUN dotnet publish src/BillingWorker/BillingWorker.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "BillingWorker.dll"]
```

- [ ] **Step 2: Escrever o docker-compose.yml**

Sem `container_name` e sem porta fixa no host (`ports: - "8080"`), senao `--scale` quebra. Postgres com healthcheck `pg_isready` e o worker com `depends_on: condition: service_healthy`.

- [ ] **Step 3: Subir com tres instancias e verificar**

Run: `docker compose up --build --scale worker=3 -d && docker compose ps`
Expected: tres containers do worker no ar; os logs mostram um unico instance id executando cada janela do job.

- [ ] **Step 4: Verificar o criterio de aceite pelo status**

Run: consultar `/jobs/billing-close/status` na porta sorteada de cada replica e conferir que as tres devolvem a mesma execucao (mesmo `lastOwner`).

- [ ] **Step 5: Derrubar o ambiente**

Run: `docker compose down -v`

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: empacotar o worker em docker e permitir scale de instancias"
```

---

### Task 8: README

**Files:**
- Create: `README.md`

- [ ] **Step 1: Escrever o README em pt-BR**

Secoes: problema, stack, escolha da ferramenta e alternativas, como rodar (local e docker com `--scale worker=3`), endpoints com exemplos de resposta, como observar a coordenacao nos logs, criterios de aceite e como verificar cada um, testes, tradeoffs, limitacoes.

- [ ] **Step 2: Conferir que os comandos do README rodam**

Run: os comandos exatos do README, em ordem.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "docs: documentar problema, execucao com scale e criterios de aceite"
```

---

## Self-Review

**Cobertura do spec:**

| Requisito do spec | Task |
|---|---|
| Rodar job com uma instancia | 5, 7 |
| Rodar app com tres instancias | 7 |
| Evitar execucao duplicada | 5 (teste dos tres schedulers) |
| Persistir estado do scheduler | 2 (tabelas QRTZ), 5 (`UsePersistentStore`) |
| Expor status do ultimo job | 4 (`FindLastAsync`), 6 (endpoint) |
| Quartz persistent store com PostgreSQL | 5 |
| Clustering habilitado | 5 |
| instanceId unico | 1 (`WorkerIdentity`), 5 (`SchedulerId`) |
| serializer JSON | 5 |
| tabela de status propria | 2 (`job_run`), 4 (repositorio) |
| logs com instanceId | 4 (job), 1 (health) |
| `GET /jobs/billing-close/status` | 6 |
| `POST /jobs/billing-close/run-now` | 6 |
| Idempotencia | 3 |
| README com `--scale worker=3` | 8 |

**Consistencia de tipos:** `BillingCloseJob.JobName`/`Key`/`RunAsync` definidos na Task 4 e consumidos nas Tasks 5 e 6; `JobRun` e os quatro metodos de `JobRunRepository` definidos na Task 4 e consumidos na 6; `BillingCloseService.ClosePendingInvoicesAsync`/`SeedPendingInvoicesAsync` definidos na Task 3 e consumidos nas 4 e 6; `WorkerIdentity.FromEnvironment` definido na Task 1 e consumido nas 4 e 5; `PostgresFixture.ConnectionString` definido na Task 2 e consumido nas 3 a 6.
