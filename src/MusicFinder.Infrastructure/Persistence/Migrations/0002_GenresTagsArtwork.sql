-- M2: genre taxonomy, DJ tags, incremental genre mapping. Seed data is inserted by GenreTaxonomySeeder (C#)
-- so that normalized names always come from the same TextNormalizer used at match time.
ALTER TABLE Genres ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Genres ADD COLUMN IsBuiltIn INTEGER NOT NULL DEFAULT 0;
ALTER TABLE CustomTags ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0;
-- 0 = genre mapping is stale. The scanner resets it when a file is (re)read; GenreMappingJob raises it.
ALTER TABLE Tracks ADD COLUMN GenreMapVersion INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IX_Genres_ParentId ON Genres(ParentId);
CREATE INDEX IX_GenreAliases_GenreId ON GenreAliases(GenreId);
CREATE INDEX IX_TrackGenres_GenreId ON TrackGenres(GenreId, TrackId);
CREATE INDEX IX_TrackCustomTags_TagId ON TrackCustomTags(TagId, TrackId);
CREATE INDEX IX_Tracks_GenreMapVersion ON Tracks(GenreMapVersion);

CREATE TABLE AppState (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
INSERT INTO AppState(Key, Value) VALUES ('TaxonomyVersion', '1');
