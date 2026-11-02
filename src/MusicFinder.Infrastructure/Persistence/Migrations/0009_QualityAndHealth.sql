-- M6: quality facts the reader now keeps, and the health hints computed from them when a row is written.
-- Existing rows have MetaVersion < 2 and are re-read once by the next scan, which fills these in.
ALTER TABLE Tracks ADD COLUMN Codec TEXT;
ALTER TABLE Tracks ADD COLUMN Channels INTEGER;
ALTER TABLE Tracks ADD COLUMN BitDepth INTEGER;
-- Set when the tags of this file could not be read (the file is still indexed by its name). NULL = read fine.
ALTER TABLE Tracks ADD COLUMN ReadError TEXT;
-- Bit mask of IssueFlags (see Domain/Enums/IssueFlags.cs); 0 = nothing suspect.
ALTER TABLE Tracks ADD COLUMN IssueFlags INTEGER NOT NULL DEFAULT 0;
CREATE INDEX IX_Tracks_IssueFlags ON Tracks(IssueFlags) WHERE IssueFlags <> 0;

-- Files of audio-like kinds the scanner does not index, counted per root so Library Health can say "not supported".
CREATE TABLE ScanUnsupported (
  RootId INTEGER NOT NULL REFERENCES LibraryRoots(Id) ON DELETE CASCADE,
  Extension TEXT NOT NULL,
  FileCount INTEGER NOT NULL,
  PRIMARY KEY (RootId, Extension)
);
