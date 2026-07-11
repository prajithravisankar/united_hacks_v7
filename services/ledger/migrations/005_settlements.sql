-- B15 settlement receipts: one append-only row per settled commitment (the "receipt" the demo shows and
-- the review audits). New migration (001-004 already applied — never edited).

CREATE TABLE settlements (
    settlement_id   BIGINT IDENTITY PRIMARY KEY,
    commitment_id   INT          NOT NULL REFERENCES commitments(commitment_id),
    settlement_type VARCHAR(16)  NOT NULL CONSTRAINT ck_settle_type CHECK (settlement_type IN ('CashOut', 'Success', 'Failure')),
    principal_cents BIGINT       NOT NULL,
    nav_cents       BIGINT       NOT NULL,
    gain_cents      BIGINT       NOT NULL,   -- signed
    carry_cents     BIGINT       NOT NULL,
    charity_cents   BIGINT       NOT NULL,
    bonus_cents     BIGINT       NOT NULL,
    take_home_cents BIGINT       NOT NULL,
    idempotency_key VARCHAR(128) NOT NULL UNIQUE,
    created_at      DATETIME2    NOT NULL CONSTRAINT df_settle_created DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_settlements_commitment ON settlements(commitment_id);
GO

CREATE TRIGGER trg_settlements_append_only ON settlements
INSTEAD OF UPDATE, DELETE AS
BEGIN
    RAISERROR('settlements is append-only', 16, 1);
END;
GO
