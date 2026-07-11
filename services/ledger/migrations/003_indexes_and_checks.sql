-- R2 review hardening. New migration (001/002 are already applied — never edited).
-- Close the known access paths (SQL Server does not auto-index FKs) and add
-- defensive non-negative money checks. delta_cents stays signed (double-entry).

CREATE INDEX ix_postings_account ON ledger_postings(account);
CREATE INDEX ix_txns_commitment  ON ledger_transactions(commitment_id);
CREATE INDEX ix_commitments_user ON commitments(user_id);
CREATE INDEX ix_verifications_ms ON verifications(milestone_id);

ALTER TABLE nav_snapshots        ADD CONSTRAINT ck_nav_nonneg  CHECK (nav_cents >= 0);
ALTER TABLE community_pool_stats ADD CONSTRAINT ck_pool_nonneg CHECK (pool_cents >= 0 AND committed_people >= 0);
GO
