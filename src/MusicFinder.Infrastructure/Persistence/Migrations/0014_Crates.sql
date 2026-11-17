-- Crates: named, ordered lists of library tracks for preparing a set. A crate only references tracks;
-- it never touches the music files. Deleting a crate or a track never deletes anything else.
CREATE TABLE Crates (
  Id INTEGER PRIMARY KEY,
  Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
  CreatedUtc TEXT NOT NULL
);

CREATE TABLE CrateTracks (
  CrateId INTEGER NOT NULL REFERENCES Crates(Id) ON DELETE CASCADE,
  TrackId INTEGER NOT NULL REFERENCES Tracks(Id) ON DELETE CASCADE,
  Position INTEGER NOT NULL,
  AddedUtc TEXT NOT NULL,
  PRIMARY KEY (CrateId, TrackId)
);
CREATE INDEX IX_CrateTracks_Order ON CrateTracks(CrateId, Position);
CREATE INDEX IX_CrateTracks_TrackId ON CrateTracks(TrackId);
