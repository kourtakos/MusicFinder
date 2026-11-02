-- M5: online identification caches.
-- The IdentificationCache created by 0001 was never written by any version of the application, so it is empty
-- everywhere; it is recreated here with file identity (size + modified time) so a changed file never reuses an old fingerprint.
DROP TABLE IdentificationCache;

CREATE TABLE IdentificationCache (
  TrackId INTEGER PRIMARY KEY REFERENCES Tracks(Id) ON DELETE CASCADE,
  FileSize INTEGER NOT NULL,
  ModifiedUtc TEXT NOT NULL,
  Fingerprint TEXT NOT NULL,
  DurationSeconds INTEGER NOT NULL,
  Algorithm INTEGER NOT NULL,
  LengthSeconds INTEGER NOT NULL,
  ComputedUtc TEXT NOT NULL
);

-- One row per question asked of an online service, keyed by the question (not by track), so two copies of a song share it.
CREATE TABLE IdentificationLookups (
  RequestKey TEXT PRIMARY KEY,
  TrackId INTEGER REFERENCES Tracks(Id) ON DELETE SET NULL,
  Method TEXT NOT NULL CHECK(Method IN ('AcoustIdFingerprint','MusicBrainzTagSearch','ExistingRecordingId')),
  Outcome TEXT NOT NULL CHECK(Outcome IN ('Found','NoMatch')),
  ResultJson TEXT NOT NULL,
  CheckedUtc TEXT NOT NULL,
  ExpiresUtc TEXT NOT NULL
);
CREATE INDEX IX_IdentificationLookups_Expires ON IdentificationLookups(ExpiresUtc);

-- MusicBrainz recordings and releases as received, by id.
CREATE TABLE MusicBrainzCache (
  EntityType TEXT NOT NULL CHECK(EntityType IN ('recording','release')),
  Mbid TEXT NOT NULL,
  Json TEXT NOT NULL,
  FetchedUtc TEXT NOT NULL,
  ExpiresUtc TEXT NOT NULL,
  PRIMARY KEY(EntityType, Mbid)
);
