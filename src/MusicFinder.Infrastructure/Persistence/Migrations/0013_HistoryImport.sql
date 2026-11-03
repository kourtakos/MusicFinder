-- M4: history imported from DJ programs (VirtualDJ, Traktor).
-- HistoryImportFiles remembers which history files were read and how big they were, so an import only re-reads files that changed.
CREATE TABLE HistoryImportFiles (
  Source TEXT NOT NULL,
  FilePath TEXT NOT NULL,
  Length INTEGER NOT NULL,
  LastWriteUtc TEXT NOT NULL,
  ImportedUtc TEXT NOT NULL,
  PRIMARY KEY (Source, FilePath)
);

-- Plays a DJ program reported that could not be tied to exactly one library track. Nothing is dropped silently:
-- the user can link each one to a track or ignore it, and pending ones are tried again on every import.
CREATE TABLE HistoryImportUnmatched (
  Id INTEGER PRIMARY KEY,
  Source TEXT NOT NULL,
  ExternalEventKey TEXT NOT NULL,
  PlayedUtc TEXT NOT NULL,
  SourcePath TEXT NOT NULL,
  Artist TEXT,
  Title TEXT,
  FileSize INTEGER,
  DurationSeconds REAL,
  Reason TEXT NOT NULL,
  CandidatesJson TEXT,
  Status TEXT NOT NULL DEFAULT 'Pending' CHECK (Status IN ('Pending', 'Linked', 'Ignored')),
  ResolvedTrackId INTEGER REFERENCES Tracks(Id) ON DELETE SET NULL,
  ResolvedUtc TEXT,
  UNIQUE (Source, ExternalEventKey)
);
CREATE INDEX IX_HistoryImportUnmatched_Status ON HistoryImportUnmatched(Status);

-- Matching a history entry to a library track: by path regardless of case, then by file name and size.
CREATE INDEX IX_Tracks_FullPathNoCase ON Tracks(FullPath COLLATE NOCASE);
CREATE INDEX IX_Tracks_FileNameSize ON Tracks(FileName COLLATE NOCASE, FileSize);
