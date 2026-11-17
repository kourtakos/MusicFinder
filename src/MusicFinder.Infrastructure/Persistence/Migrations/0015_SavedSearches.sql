-- Saved searches ("smart crates"): a query and its filters, kept under a name and run again on demand.
-- They store no tracks; opening one always shows what matches now.
CREATE TABLE SavedSearches (
  Id INTEGER PRIMARY KEY,
  Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
  StateJson TEXT NOT NULL,
  CreatedUtc TEXT NOT NULL
);
