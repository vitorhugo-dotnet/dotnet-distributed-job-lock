-- Dominio e auditoria proprios. As tabelas QRTZ_* sao do scheduler; o que o
-- negocio precisa saber ("rodou quando, em quem, com que resultado") mora aqui.

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
SELECT 'cliente-' || g, (g * 1000)::BIGINT
  FROM generate_series(1, 25) AS g;
