-- B13 state machine: optimistic concurrency on commitments + an append-only event trail.
-- New migration (001-003 are already applied — never edited).

-- ROWVERSION auto-bumps on every UPDATE; a transition writes WHERE row_version = @seen, so a
-- concurrent transition that already moved the row updates zero rows and loses (one wins).
ALTER TABLE commitments ADD row_version ROWVERSION;
GO

-- The audit trail: one append-only row per transition. Replaying these reconstructs any commitment's
-- full history. idempotency_key is UNIQUE, so re-delivering the same command is a no-op.
CREATE TABLE commitment_events (
    event_id        BIGINT IDENTITY PRIMARY KEY,
    commitment_id   INT          NOT NULL REFERENCES commitments(commitment_id),
    from_state      VARCHAR(24)  NOT NULL,
    to_state        VARCHAR(24)  NOT NULL,
    command         VARCHAR(32)  NOT NULL,
    idempotency_key VARCHAR(128) NOT NULL UNIQUE,
    occurred_at     DATETIME2    NOT NULL CONSTRAINT df_ce_occurred DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_commitment_events_commitment ON commitment_events(commitment_id, event_id);
GO

-- Append-only, like the ledger tables.
CREATE TRIGGER trg_commitment_events_append_only ON commitment_events
INSTEAD OF UPDATE, DELETE AS
BEGIN
    RAISERROR('commitment_events is append-only', 16, 1);
END;
GO
