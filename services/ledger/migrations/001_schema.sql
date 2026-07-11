-- BOYS OLTP ledger schema (SQL Server). Money is BIGINT cents everywhere; no float.
-- Product limits from idea.md are DB CHECK constraints where a single-row check can express them.

CREATE TABLE users (
    user_id       INT IDENTITY PRIMARY KEY,
    email         VARCHAR(256)  NOT NULL UNIQUE,
    display_name  NVARCHAR(128) NOT NULL,
    role          VARCHAR(16)   NOT NULL CONSTRAINT ck_users_role CHECK (role IN ('learner', 'referee', 'admin')),
    created_at    DATETIME2     NOT NULL CONSTRAINT df_users_created DEFAULT SYSUTCDATETIME()
);

CREATE TABLE charities (
    charity_id INT IDENTITY PRIMARY KEY,
    name       NVARCHAR(128) NOT NULL UNIQUE
);

CREATE TABLE commitments (
    commitment_id INT IDENTITY PRIMARY KEY,
    user_id       INT           NOT NULL REFERENCES users(user_id),
    goal_text     NVARCHAR(1000) NOT NULL,
    stake_cents   BIGINT        NOT NULL CONSTRAINT ck_commit_stake CHECK (stake_cents BETWEEN 2000 AND 50000),
    charity_id    INT           NOT NULL REFERENCES charities(charity_id),
    drive_mode    VARCHAR(8)    NOT NULL CONSTRAINT ck_commit_drive CHECK (drive_mode IN ('AUTO', 'USER')),
    state         VARCHAR(24)   NOT NULL CONSTRAINT ck_commit_state CHECK (state IN
                    ('draft','active','pending_verification','milestone_cleared','riding','cashed_out','succeeded','failed','settled')),
    created_at    DATETIME2     NOT NULL CONSTRAINT df_commit_created DEFAULT SYSUTCDATETIME(),
    deadline      DATETIME2     NOT NULL,
    -- deadline must be 1 week .. 6 months out from creation
    CONSTRAINT ck_commit_deadline CHECK (deadline >= DATEADD(DAY, 7, created_at) AND deadline <= DATEADD(MONTH, 6, created_at))
);

CREATE TABLE milestones (
    milestone_id  INT IDENTITY PRIMARY KEY,
    commitment_id INT           NOT NULL REFERENCES commitments(commitment_id),
    ordinal       INT           NOT NULL CONSTRAINT ck_ms_ordinal CHECK (ordinal BETWEEN 1 AND 5),  -- caps a commitment at 5 milestones
    description   NVARCHAR(500) NOT NULL,
    target_metric NVARCHAR(200) NOT NULL,
    due_date      DATETIME2     NOT NULL,
    state         VARCHAR(24)   NOT NULL CONSTRAINT ck_ms_state CHECK (state IN ('pending','pending_verification','cleared','failed')),
    CONSTRAINT uq_ms_ordinal UNIQUE (commitment_id, ordinal)
);

CREATE TABLE verifications (
    verification_id  INT IDENTITY PRIMARY KEY,
    milestone_id     INT          NOT NULL REFERENCES milestones(milestone_id),
    evidence_uri     NVARCHAR(500) NULL,
    ai_verdict       NVARCHAR(MAX) NULL,   -- JSON from brain
    referee_decision VARCHAR(16)  NULL CONSTRAINT ck_ver_decision CHECK (referee_decision IN ('approved','rejected')),
    submitted_at     DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME(),
    decided_at       DATETIME2    NULL
);

CREATE TABLE ledger_accounts (
    account VARCHAR(32) PRIMARY KEY   -- USER_ESCROW, ACTION_POOL, USER_YIELD, WINNERS_BONUS_POOL, CHARITY_PAYABLE, HOUSE_CARRY
);

-- Double-entry: a transaction header (idempotency lives here) + balanced posting lines.
CREATE TABLE ledger_transactions (
    txn_id          UNIQUEIDENTIFIER PRIMARY KEY,
    idempotency_key VARCHAR(128)     NOT NULL UNIQUE,
    commitment_id   INT              NULL REFERENCES commitments(commitment_id),
    created_at      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE ledger_postings (
    posting_id  BIGINT IDENTITY PRIMARY KEY,
    txn_id      UNIQUEIDENTIFIER NOT NULL REFERENCES ledger_transactions(txn_id),
    account     VARCHAR(32)      NOT NULL REFERENCES ledger_accounts(account),
    delta_cents BIGINT           NOT NULL,
    created_at  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_postings_txn ON ledger_postings(txn_id);

CREATE TABLE community_pool_stats (
    id               INT     PRIMARY KEY CONSTRAINT ck_pool_single CHECK (id = 1),  -- single row
    committed_people INT     NOT NULL,
    pool_cents       BIGINT  NOT NULL,
    updated_at       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE nav_snapshots (
    snapshot_id   BIGINT IDENTITY PRIMARY KEY,
    commitment_id INT    NOT NULL REFERENCES commitments(commitment_id),
    as_of_date    DATE   NOT NULL,
    nav_cents     BIGINT NOT NULL,
    CONSTRAINT uq_nav UNIQUE (commitment_id, as_of_date)
);
GO

-- The ledger is append-only: block UPDATE/DELETE on the money tables.
CREATE TRIGGER trg_postings_append_only ON ledger_postings
INSTEAD OF UPDATE, DELETE AS
BEGIN
    RAISERROR('ledger_postings is append-only', 16, 1);
END;
GO

CREATE TRIGGER trg_txns_append_only ON ledger_transactions
INSTEAD OF UPDATE, DELETE AS
BEGIN
    RAISERROR('ledger_transactions is append-only', 16, 1);
END;
GO
