-- Browsing and sorting by title (the default view with an empty search box) walks this index in order and stops at the page size,
-- instead of sorting the whole library on every query. The expression is exactly the sort key used by SearchSqlBuilder.
CREATE INDEX IX_Tracks_TitleSort ON Tracks((COALESCE(NULLIF(TRIM(Title), ''), FileName) COLLATE NOCASE), Id);
