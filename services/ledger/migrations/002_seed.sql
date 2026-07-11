-- Reference/seed data for the demo world.

INSERT INTO users (email, display_name, role) VALUES
    ('demo@boys.local', N'Demo User', 'learner'),
    ('referee@boys.local', N'Referee', 'referee');

INSERT INTO charities (name) VALUES
    (N'Feeding America'),
    (N'Doctors Without Borders'),
    (N'American Red Cross'),
    (N'World Wildlife Fund');

INSERT INTO ledger_accounts (account) VALUES
    ('USER_ESCROW'), ('ACTION_POOL'), ('USER_YIELD'),
    ('WINNERS_BONUS_POOL'), ('CHARITY_PAYABLE'), ('HOUSE_CARRY');

-- Seeded community-pool backdrop: "1,204 people committing · $47,300 in the pool".
INSERT INTO community_pool_stats (id, committed_people, pool_cents) VALUES (1, 1204, 4730000);
GO
