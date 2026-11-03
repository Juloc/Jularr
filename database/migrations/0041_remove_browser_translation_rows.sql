-- The translations Mozilla's browser engine once saved on the server (one row per section and language, ":translation/<language>/<NNNNN>")
-- are no longer read by anything: the Reader keeps a browser translation in the browser, and a translated EPUB is built from the server's
-- own translations (":localtranslation/..."). Only those rows leave; every other cached resource of a book stays. One set-based delete:
-- the paths share a literal prefix, so the predicate is exact under any collation.
DELETE FROM "ReadingResources" WHERE "Path" LIKE ':translation/%';
