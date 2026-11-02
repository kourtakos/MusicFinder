-- M6 slice 2: exact duplicates. A hash is computed only on request (never while indexing) and only for files that share
-- their size with another file. A row is the SHA-256 of the file's bytes as they were when FileSize/ModifiedUtc were read.
CREATE TABLE FileHashes (
  TrackId INTEGER PRIMARY KEY REFERENCES Tracks(Id) ON DELETE CASCADE,
  FileSize INTEGER NOT NULL,
  ModifiedUtc TEXT NOT NULL,
  Algorithm TEXT NOT NULL DEFAULT 'SHA256',
  Hash TEXT NOT NULL,
  ComputedUtc TEXT NOT NULL
);
CREATE INDEX IX_FileHashes_Hash ON FileHashes(Hash);

-- Any change to the bytes of a file (a tag write, a replaced file) changes its size or modified time; the remembered hash
-- is then wrong, so it is dropped. A rename or move changes neither and keeps the hash.
CREATE TRIGGER Tracks_au_hashStale AFTER UPDATE OF FileSize, ModifiedUtc ON Tracks
WHEN new.FileSize <> old.FileSize OR new.ModifiedUtc <> old.ModifiedUtc BEGIN
  DELETE FROM FileHashes WHERE TrackId = new.Id;
END;

-- "This group is not a duplicate": a group is identified by its content hash and stays dismissed on later scans.
CREATE TABLE DuplicateDismissals (
  Hash TEXT PRIMARY KEY,
  DismissedUtc TEXT NOT NULL
);
