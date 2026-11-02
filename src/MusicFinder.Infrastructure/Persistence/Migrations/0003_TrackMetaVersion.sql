-- Version of the metadata reader that produced a track's row. When the reader learns to extract more
-- (M2: artwork hash) the scanner re-reads files whose MetaVersion is lower, even if size/mtime are unchanged.
-- Existing rows start at 0 so a library indexed by an older build is re-read once.
ALTER TABLE Tracks ADD COLUMN MetaVersion INTEGER NOT NULL DEFAULT 0;
