namespace BillingWorker.Jobs;

/// <summary>
/// Uma execucao do job, do jeito que ela fica gravada em <c>job_run</c>.
/// Os horarios sao <see cref="DateTime"/> com <c>Kind = Utc</c> porque e assim
/// que o Npgsql devolve <c>timestamptz</c> — e e assim que o System.Text.Json
/// serializa terminando em <c>Z</c>.
/// </summary>
public sealed record JobRun(
    long Id,
    string JobName,
    string Owner,
    string Status,
    int ProcessedItems,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string? Error);

public static class JobRunStatus
{
    public const string Running = "RUNNING";
    public const string Success = "SUCCESS";
    public const string Failed = "FAILED";
    public const string NeverRun = "NEVER_RUN";
}
