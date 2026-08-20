# distributed-job-lock

Job agendado em .NET 10 que roda **exatamente uma vez por janela**, mesmo com três instâncias da aplicação no ar — usando Quartz.NET em modo *clustered* sobre PostgreSQL.

## Problema

Três instâncias da mesma aplicação sobem atrás de um load balancer. Todas carregam o mesmo agendamento. Sem coordenação, o fechamento de faturas roda três vezes no mesmo segundo: cobrança triplicada, e-mail triplicado, relatório triplicado.

É o tipo de bug que faz o time inteiro descobrir a palavra "idempotência" tarde demais, geralmente por telefone, geralmente num sábado.

O que este projeto entrega:

- uma única instância executa o job por janela;
- reiniciar uma instância no meio não duplica processamento;
- o estado do scheduler sobrevive a restart, porque mora no banco;
- o resultado da última execução é consultável por HTTP, de qualquer instância.

## Stack

- .NET 10 / ASP.NET Core (um host só: worker + API)
- Quartz.NET 3.19.1 — `AdoJobStore` (JobStoreTX), clustering e serializer System.Text.Json
- PostgreSQL 16 + Npgsql 10
- Dapper (consultas próprias) e DbUp (migrações)
- xUnit + Testcontainers
- Docker Compose

## Escolha da ferramenta

**Quartz.NET com persistent store e clustering.**

O modo clustered existe exatamente para várias instâncias compartilhando o mesmo job store. A aquisição do trigger é serializada por lock de linha no próprio banco (`qrtz_locks`), e o check-in periódico em `qrtz_scheduler_state` permite que o cluster perceba quando uma instância morre no meio de um job e recupere o trigger.

Ou seja: não há protocolo de lock caseiro para errar em timeout, release ou crash.

### Alternativas

| Opção | A favor | Contra |
|---|---|---|
| **Hangfire** | Dashboard pronto, API mais simples | Menos controle sobre agendamento complexo; algumas features dependem de edição paga |
| **Advisory lock manual no PostgreSQL** | Zero framework extra; ótimo para entender lock | Fácil errar timeout, release e comportamento em crash |
| **Quartz.NET clustered** (escolhida) | Robusto, próximo de produção, bom para agendamento complexo | Mais configuração; mais tabelas no banco |

O advisory lock manual não foi descartado por completo: ele é usado num lugar onde o escopo é trivial e o ganho é real — serializar a migração no boot, para as três instâncias não correrem o DbUp ao mesmo tempo (`src/BillingWorker/Persistence/DatabaseMigrator.cs`).

## Arquitetura

```
worker-a ─┐
worker-b ─┼─→ PostgreSQL ─ qrtz_*    (job store clustered)
worker-c ─┘                job_run   (auditoria da execução)
                           invoices  (domínio)
```

Todas as instâncias usam o mesmo `SchedulerName` (`billing-scheduler`) e um `SchedulerId` diferente (o hostname do container). É isso que as coloca no mesmo cluster e, ao mesmo tempo, deixa o cluster saber quem é quem.

## Como rodar

### Docker, com três instâncias

```bash
docker compose up --build --scale worker=3
```

As réplicas não têm porta fixa no host — porta fixa e `--scale` não convivem. O Docker sorteia uma porta para cada:

```bash
docker compose ps
```

```text
NAME       SERVICE    STATUS    PORTS
...worker-1  worker   Up        0.0.0.0:58222->8080/tcp
...worker-2  worker   Up        0.0.0.0:58226->8080/tcp
...worker-3  worker   Up        0.0.0.0:58224->8080/tcp
```

O job roda a cada minuto (`Billing__Cron`, padrão `0 * * * * ?`). Os logs mostram quem ganhou o trigger:

```text
worker-1  | [900923c9614b] ganhou o trigger de billing-close (run 1)
worker-1  | [900923c9614b] billing-close fechou 25 faturas
worker-3  | [ecc6f4ae59cd] ganhou o trigger de billing-close (run 2)
worker-3  | [ecc6f4ae59cd] billing-close fechou 0 faturas
```

Note que a segunda janela caiu em outra instância e fechou zero faturas: o trigger circula, o trabalho não se repete.

### Local, sem Docker

```bash
docker compose up postgres -d
dotnet run --project src/BillingWorker
```

Sobe em `http://localhost:5079`. A connection string padrão em `appsettings.json` já aponta para o Postgres do compose.

## Endpoints

| Método | Rota | O que faz |
|---|---|---|
| `GET` | `/jobs/billing-close/status` | Última execução do job |
| `POST` | `/jobs/billing-close/run-now` | Dispara o job agora |
| `POST` | `/invoices/seed?count=25` | Repõe faturas PENDING |
| `GET` | `/health` | Saúde e id da instância |

### `GET /jobs/billing-close/status`

```json
{
  "job": "billing-close",
  "lastRunAt": "2026-08-20T17:13:00.074049Z",
  "lastOwner": "900923c9614b",
  "lastStatus": "SUCCESS",
  "processedItems": 25
}
```

Antes da primeira execução, `lastStatus` vem como `NEVER_RUN` e os demais campos nulos.

Qualquer réplica responde a mesma coisa — o estado está no banco, não no processo.

### `POST /jobs/billing-close/run-now`

```json
{ "job": "billing-close", "requestedBy": "ecc6f4ae59cd" }
```

Devolve `202 Accepted`. O `requestedBy` é quem **recebeu** o POST, não necessariamente quem vai **executar**: o disparo entra no job store compartilhado e qualquer instância do cluster pode adquiri-lo. Isso não é um detalhe infeliz da implementação — é a demonstração mais curta de que a coordenação está no banco.

## Critérios de aceite

| Critério | Como verificar |
|---|---|
| Com 1 instância, job executa normalmente | `docker compose up` e observar o log a cada minuto |
| Com 3 instâncias, apenas uma processa o trigger | `docker compose up --scale worker=3`; `SELECT owner, processed_items FROM job_run` mostra **uma** linha por janela |
| Reiniciar uma instância não duplica processamento | `docker compose restart worker-1` e disparar `run-now`: a execução seguinte fecha as faturas pendentes uma única vez |
| Job store mantém histórico | Tabelas `qrtz_*` sobrevivem ao restart; `job_run` guarda toda execução com dono, status e contagem |
| README mostra o comando de scale | `docker compose up --build --scale worker=3` |

Verificação direta no banco:

```bash
docker compose exec postgres psql -U billing -d billing \
  -c "SELECT owner, status, processed_items FROM job_run ORDER BY id;"

docker compose exec postgres psql -U billing -d billing \
  -c "SELECT instance_name FROM qrtz_scheduler_state;"
```

A segunda consulta é a prova de que o clustering está de fato ligado: com três réplicas no ar, ela devolve três linhas.

## Idempotência

O fechamento é uma única instrução:

```sql
UPDATE invoices
   SET status = 'CLOSED', closed_at = now(), closed_by = @owner
 WHERE status = 'PENDING'
```

O filtro vive dentro do mesmo `UPDATE` que muda o status, então não existe janela entre ler e marcar. Se o job rodar duas vezes — por bug, por retry, por um cluster mal configurado — a segunda execução processa zero faturas em vez de cobrar o cliente de novo.

O lock distribuído evita o trabalho duplicado. A idempotência garante que, quando o lock falhar, ninguém é cobrado duas vezes. As duas coisas juntas, não uma no lugar da outra.

## Testes

```bash
dotnet test
```

25 testes. Os de integração sobem um PostgreSQL real via Testcontainers — **Docker precisa estar no ar**. Testar job store clustered contra um fake em memória não provaria nada.

Os que importam:

- `ClusteredSchedulerTests.ApenasUmaInstanciaExecutaOTrigger` — três schedulers, o mesmo job store, um trigger: exatamente uma execução. O teste também confirma que as três instâncias são distintas e que aparecem em `qrtz_scheduler_state`, senão passaria sem clustering nenhum.
- `ClusteredSchedulerTests.ReiniciarUmaInstanciaNaoDuplicaProcessamento` — derruba e recria uma instância no meio.
- `BillingCloseServiceTests.SegundaExecucaoNaoProcessaNada` — idempotência.
- `BillingCloseJobTests.RegistraFalhaQuandoOFechamentoQuebra` — falha vira `FAILED` com mensagem, não silêncio.

## Limitações conhecidas

- Sem dashboard. Para inspeção visual, a alternativa é Hangfire.
- Sem retry com backoff: uma falha fica registrada como `FAILED` e espera a próxima janela.
- Se uma instância morre no meio do job, a linha `RUNNING` fica órfã em `job_run` até o cluster detectar a morte. É informação, não lixo — mas não há limpeza automática.
- `misfire` está configurado como `DoNothing`: janela perdida é janela pulada, não acumulada. Para cobrança recorrente isso é o comportamento certo; para outros domínios, talvez não.
