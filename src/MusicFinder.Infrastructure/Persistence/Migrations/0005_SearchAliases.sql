-- M7: smart Greek search. Per-track normalized (N) and phonetic-key (K) text per field, kept in a second FTS5 index.
-- TrackSearch (0001) is left untouched: it serves exact matches and works while alias rows are still being built.

-- 0 = the alias row is missing or stale; the SearchAliasIndexJob builds it and stamps the current SearchTextBuilder version.
ALTER TABLE Tracks ADD COLUMN SearchAliasVersion INTEGER NOT NULL DEFAULT 0;
CREATE INDEX IX_Tracks_SearchAliasVersion ON Tracks(SearchAliasVersion);

CREATE TABLE TrackSearchAliases (
  TrackId INTEGER PRIMARY KEY REFERENCES Tracks(Id) ON DELETE CASCADE,
  TitleN TEXT NOT NULL DEFAULT '', ArtistN TEXT NOT NULL DEFAULT '', AlbumN TEXT NOT NULL DEFAULT '',
  GenreN TEXT NOT NULL DEFAULT '', FileN  TEXT NOT NULL DEFAULT '',
  TitleK TEXT NOT NULL DEFAULT '', ArtistK TEXT NOT NULL DEFAULT '', AlbumK TEXT NOT NULL DEFAULT '',
  GenreK TEXT NOT NULL DEFAULT '', FileK  TEXT NOT NULL DEFAULT '',
  Extra  TEXT NOT NULL DEFAULT ''   -- the user's alias overrides that apply to this track (normalized and key forms)
);

-- The text is normalized in C#, so the tokenizer must not fold anything itself (remove_diacritics 0).
CREATE VIRTUAL TABLE TrackAliasSearch USING fts5(
  TitleN, ArtistN, AlbumN, GenreN, FileN, TitleK, ArtistK, AlbumK, GenreK, FileK, Extra,
  content='TrackSearchAliases', content_rowid='TrackId',
  tokenize='unicode61 remove_diacritics 0',
  prefix='2 3'
);

-- Triggers replace rows with DELETE + INSERT, never a conflict clause (see docs/database.md).
CREATE TRIGGER TrackSearchAliases_ai AFTER INSERT ON TrackSearchAliases BEGIN
  INSERT INTO TrackAliasSearch(rowid, TitleN, ArtistN, AlbumN, GenreN, FileN, TitleK, ArtistK, AlbumK, GenreK, FileK, Extra)
  VALUES (new.TrackId, new.TitleN, new.ArtistN, new.AlbumN, new.GenreN, new.FileN, new.TitleK, new.ArtistK, new.AlbumK, new.GenreK, new.FileK, new.Extra);
END;

CREATE TRIGGER TrackSearchAliases_ad AFTER DELETE ON TrackSearchAliases BEGIN
  INSERT INTO TrackAliasSearch(TrackAliasSearch, rowid, TitleN, ArtistN, AlbumN, GenreN, FileN, TitleK, ArtistK, AlbumK, GenreK, FileK, Extra)
  VALUES ('delete', old.TrackId, old.TitleN, old.ArtistN, old.AlbumN, old.GenreN, old.FileN, old.TitleK, old.ArtistK, old.AlbumK, old.GenreK, old.FileK, old.Extra);
END;

CREATE TRIGGER TrackSearchAliases_au AFTER UPDATE ON TrackSearchAliases BEGIN
  INSERT INTO TrackAliasSearch(TrackAliasSearch, rowid, TitleN, ArtistN, AlbumN, GenreN, FileN, TitleK, ArtistK, AlbumK, GenreK, FileK, Extra)
  VALUES ('delete', old.TrackId, old.TitleN, old.ArtistN, old.AlbumN, old.GenreN, old.FileN, old.TitleK, old.ArtistK, old.AlbumK, old.GenreK, old.FileK, old.Extra);
  INSERT INTO TrackAliasSearch(rowid, TitleN, ArtistN, AlbumN, GenreN, FileN, TitleK, ArtistK, AlbumK, GenreK, FileK, Extra)
  VALUES (new.TrackId, new.TitleN, new.ArtistN, new.AlbumN, new.GenreN, new.FileN, new.TitleK, new.ArtistK, new.AlbumK, new.GenreK, new.FileK, new.Extra);
END;

-- When a track is deleted, remove its alias row FIRST so the FTS 'delete' command sees the old values. Relying on the foreign-key
-- cascade is not enough: cascaded deletes are not guaranteed to run the alias triggers before the parent row is gone.
CREATE TRIGGER Tracks_bd_aliases BEFORE DELETE ON Tracks BEGIN
  DELETE FROM TrackSearchAliases WHERE TrackId = old.Id;
END;

-- Any change to the text a track is searched by makes its alias row stale, whichever code wrote it (safety net: the
-- repositories also reset the version explicitly). Stamping SearchAliasVersion itself does not list these columns, so it does not fire this.
CREATE TRIGGER Tracks_au_aliasStale AFTER UPDATE OF Title, Artist, AlbumArtist, Album, GenreRaw, FileName, FullPath ON Tracks
WHEN new.SearchAliasVersion <> 0 BEGIN
  UPDATE Tracks SET SearchAliasVersion = 0 WHERE Id = new.Id;
END;

CREATE TABLE SearchAliasOverrides (
  Id INTEGER PRIMARY KEY,
  Source TEXT NOT NULL,
  Alias TEXT NOT NULL,
  SourceNormalized TEXT NOT NULL,
  AliasNormalized TEXT NOT NULL,
  CreatedUtc TEXT NOT NULL,
  UNIQUE(SourceNormalized, AliasNormalized)
);

-- Read-only list of the distinct words in the index, used to suggest typo corrections without scanning tracks.
CREATE VIRTUAL TABLE TrackAliasVocab USING fts5vocab('TrackAliasSearch', 'col');
