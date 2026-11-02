CREATE TABLE LibraryRoots (
  Id INTEGER PRIMARY KEY,
  Path TEXT NOT NULL UNIQUE,
  Enabled INTEGER NOT NULL DEFAULT 1,
  IncludeSubfolders INTEGER NOT NULL DEFAULT 1,
  LastScanUtc TEXT,
  Status TEXT NOT NULL DEFAULT 'Pending'
);
CREATE TABLE Folders (
  Id INTEGER PRIMARY KEY,
  RootId INTEGER NOT NULL REFERENCES LibraryRoots(Id),
  ParentId INTEGER REFERENCES Folders(Id),
  FullPath TEXT NOT NULL UNIQUE,
  Name TEXT NOT NULL,
  DirectTrackCount INTEGER NOT NULL DEFAULT 0,
  RecursiveTrackCount INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IX_Folders_ParentId ON Folders(ParentId);
CREATE INDEX IX_Folders_RootId ON Folders(RootId);
CREATE TABLE Tracks (
  Id INTEGER PRIMARY KEY,
  FolderId INTEGER NOT NULL REFERENCES Folders(Id),
  FullPath TEXT NOT NULL UNIQUE,
  FileName TEXT NOT NULL,
  Extension TEXT NOT NULL,
  FileSize INTEGER NOT NULL,
  ModifiedUtc TEXT NOT NULL,
  IndexedUtc TEXT NOT NULL,
  Title TEXT, Artist TEXT, AlbumArtist TEXT, Album TEXT,
  GenreRaw TEXT, Year INTEGER, TrackNumber INTEGER,
  DurationMs INTEGER, BitrateKbps INTEGER, SampleRateHz INTEGER,
  Bpm REAL, MusicalKey TEXT, MusicBrainzRecordingId TEXT,
  ArtworkCacheKey TEXT, Missing INTEGER NOT NULL DEFAULT 0,
  FingerprintHash TEXT, ContentHash TEXT,
  NormalizedText TEXT
);
CREATE INDEX IX_Tracks_FolderId ON Tracks(FolderId);
CREATE INDEX IX_Tracks_ModifiedUtc ON Tracks(ModifiedUtc);
CREATE INDEX IX_Tracks_Title ON Tracks(Title COLLATE NOCASE);
CREATE INDEX IX_Tracks_Artist ON Tracks(Artist COLLATE NOCASE);
CREATE INDEX IX_Tracks_Bpm ON Tracks(Bpm);
CREATE INDEX IX_Tracks_Year ON Tracks(Year);
CREATE INDEX IX_Tracks_Missing ON Tracks(Missing);
CREATE TABLE Genres (
  Id INTEGER PRIMARY KEY,
  ParentId INTEGER REFERENCES Genres(Id),
  Name TEXT NOT NULL,
  NormalizedName TEXT NOT NULL UNIQUE
);
CREATE TABLE GenreAliases (
  AliasNormalized TEXT PRIMARY KEY,
  GenreId INTEGER NOT NULL REFERENCES Genres(Id)
);
CREATE TABLE TrackGenres (
  TrackId INTEGER NOT NULL REFERENCES Tracks(Id) ON DELETE CASCADE,
  GenreId INTEGER NOT NULL REFERENCES Genres(Id),
  Source TEXT NOT NULL,
  PRIMARY KEY(TrackId, GenreId, Source)
);
CREATE TABLE CustomTags (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE);
CREATE TABLE TrackCustomTags (
  TrackId INTEGER NOT NULL REFERENCES Tracks(Id) ON DELETE CASCADE,
  TagId INTEGER NOT NULL REFERENCES CustomTags(Id),
  PRIMARY KEY(TrackId,TagId)
);
CREATE TABLE DjSessions (
  Id INTEGER PRIMARY KEY,
  StartedUtc TEXT NOT NULL,
  EndedUtc TEXT,
  DjSoftware TEXT,
  Name TEXT
);
CREATE TABLE TrackEvents (
  Id INTEGER PRIMARY KEY,
  TrackId INTEGER REFERENCES Tracks(Id),
  SessionId INTEGER REFERENCES DjSessions(Id),
  EventType TEXT NOT NULL,
  OccurredUtc TEXT NOT NULL,
  Source TEXT NOT NULL,
  Confidence REAL,
  EvidenceJson TEXT
);
CREATE INDEX IX_TrackEvents_SessionTrackTime ON TrackEvents(SessionId,TrackId,OccurredUtc);
CREATE TABLE PlayHistory (
  Id INTEGER PRIMARY KEY,
  TrackId INTEGER NOT NULL REFERENCES Tracks(Id),
  SessionId INTEGER REFERENCES DjSessions(Id),
  PlayedUtc TEXT NOT NULL,
  Status TEXT NOT NULL CHECK(Status IN ('Possible','Confirmed')),
  Source TEXT NOT NULL,
  ExternalEventKey TEXT,
  RelatedEventId INTEGER REFERENCES TrackEvents(Id),
  UNIQUE(Source,ExternalEventKey)
);
CREATE INDEX IX_PlayHistory_TrackTime ON PlayHistory(TrackId,PlayedUtc DESC);
CREATE TABLE IdentificationCache (
  TrackId INTEGER PRIMARY KEY REFERENCES Tracks(Id),
  Fingerprint TEXT, DurationSeconds INTEGER,
  AcoustId TEXT, MusicBrainzRecordingId TEXT,
  ResponseJson TEXT, CheckedUtc TEXT
);
CREATE TABLE FileOperations (
  Id INTEGER PRIMARY KEY,
  BatchId TEXT NOT NULL, TrackId INTEGER REFERENCES Tracks(Id),
  OperationType TEXT NOT NULL,
  OldPath TEXT, NewPath TEXT,
  BeforeMetadataJson TEXT,
  State TEXT NOT NULL,
  CreatedUtc TEXT NOT NULL, CompletedUtc TEXT
);

CREATE VIRTUAL TABLE TrackSearch USING fts5(
  FileName, Title, Artist, AlbumArtist, Album, GenreRaw, FullPath, NormalizedText,
  content='Tracks', content_rowid='Id',
  tokenize='unicode61 remove_diacritics 2'
);

CREATE TRIGGER Tracks_ai AFTER INSERT ON Tracks BEGIN
  INSERT INTO TrackSearch(rowid, FileName, Title, Artist, AlbumArtist, Album, GenreRaw, FullPath, NormalizedText)
  VALUES (new.Id, new.FileName, new.Title, new.Artist, new.AlbumArtist, new.Album, new.GenreRaw, new.FullPath, new.NormalizedText);
END;
CREATE TRIGGER Tracks_ad AFTER DELETE ON Tracks BEGIN
  INSERT INTO TrackSearch(TrackSearch, rowid, FileName, Title, Artist, AlbumArtist, Album, GenreRaw, FullPath, NormalizedText)
  VALUES ('delete', old.Id, old.FileName, old.Title, old.Artist, old.AlbumArtist, old.Album, old.GenreRaw, old.FullPath, old.NormalizedText);
END;
CREATE TRIGGER Tracks_au AFTER UPDATE ON Tracks BEGIN
  INSERT INTO TrackSearch(TrackSearch, rowid, FileName, Title, Artist, AlbumArtist, Album, GenreRaw, FullPath, NormalizedText)
  VALUES ('delete', old.Id, old.FileName, old.Title, old.Artist, old.AlbumArtist, old.Album, old.GenreRaw, old.FullPath, old.NormalizedText);
  INSERT INTO TrackSearch(rowid, FileName, Title, Artist, AlbumArtist, Album, GenreRaw, FullPath, NormalizedText)
  VALUES (new.Id, new.FileName, new.Title, new.Artist, new.AlbumArtist, new.Album, new.GenreRaw, new.FullPath, new.NormalizedText);
END;
INSERT INTO TrackSearch(TrackSearch) VALUES ('rebuild');
