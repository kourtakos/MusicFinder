-- Estimates of BPM and key made from the audio itself. They are kept apart from the tags in Tracks: an estimate is not a tag value
-- until the user writes it. A row counts only while the file's size and modified time and the analyzer version are unchanged.
CREATE TABLE TrackAnalysis (
  TrackId INTEGER PRIMARY KEY REFERENCES Tracks(Id) ON DELETE CASCADE,
  FileSize INTEGER NOT NULL,
  ModifiedUtc TEXT NOT NULL,
  AnalyzerVersion INTEGER NOT NULL,
  BpmStatus TEXT NOT NULL CHECK(BpmStatus IN ('Estimated','NoEstimate')),
  BpmEstimate REAL,
  BpmScore REAL,
  BpmLevel TEXT,
  BpmFlags TEXT,
  BpmAlternativesJson TEXT,
  KeyStatus TEXT NOT NULL CHECK(KeyStatus IN ('Estimated','NoEstimate')),
  KeyEstimate TEXT,
  KeyScore REAL,
  KeyLevel TEXT,
  KeyRunner TEXT,
  KeyFlags TEXT,
  TuningCents REAL,
  AnalyzedSeconds REAL NOT NULL,
  Decoder TEXT NOT NULL,
  Message TEXT,
  AnalyzedUtc TEXT NOT NULL
);
