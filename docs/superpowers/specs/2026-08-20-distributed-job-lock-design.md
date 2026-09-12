# distributed-job-lock (.NET) — Design

Data: 2026-08-20
Pasta: `dotnet-distributed-job-lock`

## Problema

Tres instancias da mesma aplicacao sobem atras de um load balancer. Todas
carregam o mesmo agendamento de fechamento de faturas. Sem coordenacao, o job
roda tres vezes na mesma janela: cobranca triplicada, relatorio triplicado.

O projeto entrega:

- uma unica instancia executa o trigger por janela;
- reiniciar uma instancia nao duplica processamento;
- o estado do scheduler sobrevive a restart (job store persistente);
- o resultado da ultima execucao e consultavel por HTTP.

## Stack

- .NET 10 (`net10.0`)
- ASP.NET Core — host unico que roda o worker (Quartz hosted service) e expoe
  os endpoints minimal API
- Quartz.NET 3.19.1 — `AdoJobStore` (JobStoreTX) + clustering + serializer JSON
- PostgreSQL 16 + Npgsql 10.0.3
- Dapper 2.1.79 para as consultas proprias
- DbUp 7.0.1 para as migracoes SQL
- xUnit + Testcontainers 4.14.0
- Docker Compose

## Escolha principal: Quartz.NET clustered

O modo clustered do Quartz existe exatamente para varias instancias
compartilhando o mesmo job store. A aquisicao do trigger e feita com lock de
linha no proprio banco (`QRTZ_LOCKS`), entao nao ha protocolo de lock caseiro
para errar em timeout, release ou crash.

Alternativas descartadas:

- **Hangfire** — mais simples e com dashboard pronto, mas o objetivo aqui e
  mostrar coordenacao explicita de scheduler, nao um painel.
- **Advisory lock manual no PostgreSQL** — zero framework extra, mas exige
  acertar timeout, release e comportamento em crash na mao. Usado neste
  projeto apenas para serializar a migracao no boot, onde o escopo e trivial.

## Arquitetura

```
worker-a ─┐
worker-b ─┼─→ PostgreSQL ─ tabelas QRTZ_* (job store clustered)
worker-c ─┘                tabela job_run  (auditoria da execucao)
                           tabela invoices (dominio)
```

Cada instancia sobe um `IScheduler` com o mesmo `InstanceName`
(`billing-scheduler`) e um `InstanceId` unico (hostname do container). Todas
veem o mesmo trigger; o job store decide qual delas o adquire.

## Componentes

### `WorkerIdentity`
Singleton com o id da instancia: `WORKER_ID` do ambiente se definido, senao
`Environment.MachineName`. E o `InstanceId` do Quartz, o `owner` gravado em
`job_run` e o prefixo dos logs.

### `DatabaseMigrator`
Roda no boot, antes do scheduler. Abre conexao, pega `pg_advisory_lock` numa
chave fixa (para tres instancias subindo juntas nao corrarem migracao), roda o
DbUp com os scripts embutidos, solta o lock.

Scripts:
- `0001_quartz_tables.sql` — schema oficial do Quartz para PostgreSQL.
- `0002_billing.sql` — `invoices` e `job_run`, mais seed de faturas PENDING.

### `InvoiceRepository` / `BillingCloseService`
`ClosePendingInvoices(owner)` executa uma unica instrucao:

```sql
UPDATE invoices SET status = 'CLOSED', closed_at = now(), closed_by = @owner
WHERE status = 'PENDING' RETURNING id
```

Idempotente por construcao: uma segunda execucao encontra zero linhas PENDING
e processa zero itens. E atomica — nao existe janela entre ler e marcar.

### `JobRunRepository`
Tabela `job_run`: `id`, `job_name`, `owner`, `status`, `processed_items`,
`started_at`, `finished_at`, `error`.

`StartRun` insere com status `RUNNING`; `FinishRun` atualiza para `SUCCESS` ou
`FAILED`. O endpoint de status le a linha mais recente por `started_at`.

### `BillingCloseJob`
`IJob` anotado com `[DisallowConcurrentExecution]`. Fluxo: registra o inicio,
fecha as faturas pendentes, registra o fim com a contagem. Excecao vira
`FAILED` com a mensagem, e o job nao e re-enfileirado (nao lanca
`JobExecutionException` com refire).

Trigger cron configuravel (`Billing:Cron`, default a cada minuto) com
`WithMisfireHandlingInstructionDoNothing`: se a janela passou porque ninguem
estava no ar, ela e pulada, nao acumulada.

### Endpoints

| Metodo | Rota | Resposta |
|---|---|---|
| GET | `/jobs/billing-close/status` | 200 com o ultimo run |
| POST | `/jobs/billing-close/run-now` | 202, dispara o trigger |
| POST | `/invoices/seed` | 202, cria N faturas PENDING (para redemonstrar) |
| GET | `/health` | 200 |

Status quando nunca rodou: `lastStatus: "NEVER_RUN"`, demais campos nulos.

`run-now` chama `scheduler.TriggerJob`. Em modo clustered isso enfileira o
trigger no banco — a execucao pode cair em qualquer instancia, inclusive uma
diferente da que recebeu o POST. Isso e o comportamento correto e a
demonstracao mais direta da coordenacao.

## Fluxo de dados

```
cron/POST run-now → job store → uma instancia adquire o trigger
  → job_run (RUNNING, owner)
  → UPDATE invoices PENDING → CLOSED
  → job_run (SUCCESS, processed_items)
GET status → ultima linha de job_run
```

## Erros

- Falha no job: `job_run` grava `FAILED` + mensagem; as faturas ja fechadas
  permanecem fechadas (o UPDATE e transacional por si).
- Instancia morre no meio: `DisallowConcurrentExecution` mantem o lock ate o
  cluster detectar a instancia morta (`ClusterCheckinInterval` 10s); outra
  instancia recupera o trigger. A linha `RUNNING` orfã fica visivel na
  auditoria — e informacao, nao lixo.
- Banco fora do ar no boot: a aplicacao falha rapido, o compose reinicia.

## Testes

Integracao com Testcontainers (PostgreSQL real, um container por classe):

1. fecha as faturas pendentes e devolve a contagem;
2. segunda execucao processa zero (idempotencia);
3. grava owner e status SUCCESS em `job_run`;
4. job com falha grava FAILED com a mensagem;
5. **dois schedulers clustered no mesmo banco, um unico trigger → exatamente
   uma execucao** (o teste que justifica o projeto);
6. endpoint de status devolve a ultima execucao; endpoint de status sem
   execucao devolve NEVER_RUN.

## Docker

`docker compose up --scale worker=3`. O servico `worker` publica a porta 8080
sem porta fixa no host (`ports: - "8080"`), o que permite escalar; `docker
compose ps` mostra as portas sorteadas. Postgres com healthcheck e o worker
com `depends_on: service_healthy`.

## Fora de escopo

Dashboard, autenticacao, retry com backoff, metricas Prometheus, multiplos
jobs.
