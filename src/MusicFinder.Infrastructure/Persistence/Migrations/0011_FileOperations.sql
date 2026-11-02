-- M6 slice 3: the journal of file renames (and later moves). The table from 0001 was never written, so it is replaced.
-- Rule: a row is written as InProgress BEFORE the file is touched, so a crash always leaves a trace that recovery can read.
DROP TABLE FileOperations;

CREATE TABLE FileOperationBatches (
  Id TEXT PRIMARY KEY,
  Kind TEXT NOT NULL,
  Template TEXT,
  CreatedUtc TEXT NOT NULL
);

-- State: Planned -> InProgress -> Done | Failed | Skipped ; Done -> UndoInProgress -> Undone (or back to Done).
-- Role: Track (its Tracks row follows the file) or Sidecar (a .lrc/.cue travelling with it).
CREATE TABLE FileOperations (
  Id INTEGER PRIMARY KEY,
  BatchId TEXT NOT NULL REFERENCES FileOperationBatches(Id) ON DELETE CASCADE,
  TrackId INTEGER NOT NULL,
  Role TEXT NOT NULL,
  OldPath TEXT NOT NULL,
  NewPath TEXT NOT NULL,
  State TEXT NOT NULL,
  Error TEXT,
  CreatedUtc TEXT NOT NULL,
  CompletedUtc TEXT
);
CREATE INDEX IX_FileOperations_Batch ON FileOperations(BatchId, Id);
CREATE INDEX IX_FileOperations_State ON FileOperations(State);
