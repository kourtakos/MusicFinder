-- M6 slice 5: "this is not a duplicate / not the same song" decisions for probable duplicates and other-version groups.
-- A key names the song (and for same-recording groups the version): the groups themselves are never stored.
CREATE TABLE ProbableDismissals (
  Key TEXT PRIMARY KEY,
  DismissedUtc TEXT NOT NULL
);
