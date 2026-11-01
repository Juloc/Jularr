-- Why a running download does not move (no peers, no tunnel). It is a hint, not a decision for the Owner: unlike AttentionKey it sends no notice and offers no retry,
-- and the job clears it as soon as data flows again.
ALTER TABLE "Operations" ADD COLUMN "HintKey" varchar(64) NULL;
