-- M3: DJ sessions and play history.
-- DjSessions, TrackEvents and PlayHistory already exist (0001). This adds what M3 and M4 need on top of them.

-- A Confirmed play (manual, or from M4's history importers) supersedes the matching Possible one. The Possible row and
-- its originating Exported event stay as evidence, but are not counted twice.
ALTER TABLE PlayHistory ADD COLUMN SupersededByPlayId INTEGER REFERENCES PlayHistory(Id);
ALTER TABLE DjSessions ADD COLUMN AutoStarted INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IX_PlayHistory_SessionTrack ON PlayHistory(SessionId, TrackId);
CREATE INDEX IX_PlayHistory_Related ON PlayHistory(RelatedEventId);
CREATE INDEX IX_TrackEvents_TrackTime ON TrackEvents(TrackId, OccurredUtc DESC);
CREATE INDEX IX_DjSessions_Active ON DjSessions(EndedUtc) WHERE EndedUtc IS NULL;

-- One row per played track, kept in step by the triggers below, so the library query joins it instead of running
-- correlated sub-queries per row (sorting 100k tracks by last played must be a join, not 100k probes).
-- Timestamps in the M3 tables are written as fixed-width ISO-8601 UTC ("yyyy-MM-ddTHH:mm:ss.fffffffZ"), so text order
-- is chronological order.
CREATE TABLE TrackPlayStats (
  TrackId INTEGER PRIMARY KEY REFERENCES Tracks(Id) ON DELETE CASCADE,
  LastConfirmedUtc TEXT,
  LastPossibleUtc TEXT,
  LastPlayedUtc TEXT,
  LastStatus TEXT,
  ConfirmedCount INTEGER NOT NULL DEFAULT 0,
  PossibleCount INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IX_TrackPlayStats_LastPlayed ON TrackPlayStats(LastPlayedUtc);
CREATE INDEX IX_TrackPlayStats_LastConfirmed ON TrackPlayStats(LastConfirmedUtc);
CREATE INDEX IX_TrackPlayStats_LastPossible ON TrackPlayStats(LastPossibleUtc);

-- Each trigger recomputes just the affected track from its non-superseded plays.
-- IMPORTANT: the statistics row is replaced with DELETE + INSERT, never "INSERT OR REPLACE". In SQLite a conflict clause on
-- the statement that fires a trigger (e.g. an importer's "INSERT OR IGNORE INTO PlayHistory") overrides the conflict
-- clauses written inside the trigger body, which would silently turn OR REPLACE into OR IGNORE and freeze the statistics.
CREATE TRIGGER PlayHistory_ai AFTER INSERT ON PlayHistory BEGIN
  DELETE FROM TrackPlayStats WHERE TrackId = new.TrackId;
  INSERT INTO TrackPlayStats(TrackId, LastConfirmedUtc, LastPossibleUtc, LastPlayedUtc, LastStatus, ConfirmedCount, PossibleCount)
  SELECT new.TrackId,
         MAX(CASE WHEN Status = 'Confirmed' THEN PlayedUtc END),
         MAX(CASE WHEN Status = 'Possible' THEN PlayedUtc END),
         MAX(PlayedUtc),
         (SELECT p2.Status FROM PlayHistory p2 WHERE p2.TrackId = new.TrackId AND p2.SupersededByPlayId IS NULL
            ORDER BY p2.PlayedUtc DESC, p2.Id DESC LIMIT 1),
         COALESCE(SUM(Status = 'Confirmed'), 0),
         COALESCE(SUM(Status = 'Possible'), 0)
  FROM PlayHistory WHERE TrackId = new.TrackId AND SupersededByPlayId IS NULL;
  DELETE FROM TrackPlayStats WHERE TrackId = new.TrackId AND ConfirmedCount = 0 AND PossibleCount = 0;
END;

CREATE TRIGGER PlayHistory_au AFTER UPDATE ON PlayHistory BEGIN
  DELETE FROM TrackPlayStats WHERE TrackId = new.TrackId;
  INSERT INTO TrackPlayStats(TrackId, LastConfirmedUtc, LastPossibleUtc, LastPlayedUtc, LastStatus, ConfirmedCount, PossibleCount)
  SELECT new.TrackId,
         MAX(CASE WHEN Status = 'Confirmed' THEN PlayedUtc END),
         MAX(CASE WHEN Status = 'Possible' THEN PlayedUtc END),
         MAX(PlayedUtc),
         (SELECT p2.Status FROM PlayHistory p2 WHERE p2.TrackId = new.TrackId AND p2.SupersededByPlayId IS NULL
            ORDER BY p2.PlayedUtc DESC, p2.Id DESC LIMIT 1),
         COALESCE(SUM(Status = 'Confirmed'), 0),
         COALESCE(SUM(Status = 'Possible'), 0)
  FROM PlayHistory WHERE TrackId = new.TrackId AND SupersededByPlayId IS NULL;
  DELETE FROM TrackPlayStats WHERE TrackId = new.TrackId AND ConfirmedCount = 0 AND PossibleCount = 0;
END;

-- A play that moved to another track also changes the old track. Kept as its own trigger because an aggregate query
-- without GROUP BY always yields one row, so it cannot be made conditional inside the trigger above.
CREATE TRIGGER PlayHistory_au_moved AFTER UPDATE OF TrackId ON PlayHistory WHEN old.TrackId <> new.TrackId BEGIN
  DELETE FROM TrackPlayStats WHERE TrackId = old.TrackId;
  INSERT INTO TrackPlayStats(TrackId, LastConfirmedUtc, LastPossibleUtc, LastPlayedUtc, LastStatus, ConfirmedCount, PossibleCount)
  SELECT old.TrackId,
         MAX(CASE WHEN Status = 'Confirmed' THEN PlayedUtc END),
         MAX(CASE WHEN Status = 'Possible' THEN PlayedUtc END),
         MAX(PlayedUtc),
         (SELECT p2.Status FROM PlayHistory p2 WHERE p2.TrackId = old.TrackId AND p2.SupersededByPlayId IS NULL
            ORDER BY p2.PlayedUtc DESC, p2.Id DESC LIMIT 1),
         COALESCE(SUM(Status = 'Confirmed'), 0),
         COALESCE(SUM(Status = 'Possible'), 0)
  FROM PlayHistory WHERE TrackId = old.TrackId AND SupersededByPlayId IS NULL;
  DELETE FROM TrackPlayStats WHERE TrackId = old.TrackId AND ConfirmedCount = 0 AND PossibleCount = 0;
END;

CREATE TRIGGER PlayHistory_ad AFTER DELETE ON PlayHistory BEGIN
  DELETE FROM TrackPlayStats WHERE TrackId = old.TrackId;
  INSERT INTO TrackPlayStats(TrackId, LastConfirmedUtc, LastPossibleUtc, LastPlayedUtc, LastStatus, ConfirmedCount, PossibleCount)
  SELECT old.TrackId,
         MAX(CASE WHEN Status = 'Confirmed' THEN PlayedUtc END),
         MAX(CASE WHEN Status = 'Possible' THEN PlayedUtc END),
         MAX(PlayedUtc),
         (SELECT p2.Status FROM PlayHistory p2 WHERE p2.TrackId = old.TrackId AND p2.SupersededByPlayId IS NULL
            ORDER BY p2.PlayedUtc DESC, p2.Id DESC LIMIT 1),
         COALESCE(SUM(Status = 'Confirmed'), 0),
         COALESCE(SUM(Status = 'Possible'), 0)
  FROM PlayHistory WHERE TrackId = old.TrackId AND SupersededByPlayId IS NULL;
  DELETE FROM TrackPlayStats WHERE TrackId = old.TrackId AND ConfirmedCount = 0 AND PossibleCount = 0;
END;

-- Rows that exist already (none in a database created by M1/M2, but cheap and keeps the invariant true).
INSERT INTO TrackPlayStats(TrackId, LastConfirmedUtc, LastPossibleUtc, LastPlayedUtc, LastStatus, ConfirmedCount, PossibleCount)
SELECT p.TrackId,
       MAX(CASE WHEN p.Status = 'Confirmed' THEN p.PlayedUtc END),
       MAX(CASE WHEN p.Status = 'Possible' THEN p.PlayedUtc END),
       MAX(p.PlayedUtc),
       (SELECT p2.Status FROM PlayHistory p2 WHERE p2.TrackId = p.TrackId AND p2.SupersededByPlayId IS NULL
          ORDER BY p2.PlayedUtc DESC, p2.Id DESC LIMIT 1),
       SUM(p.Status = 'Confirmed'),
       SUM(p.Status = 'Possible')
FROM PlayHistory p WHERE p.SupersededByPlayId IS NULL GROUP BY p.TrackId;
