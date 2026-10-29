-- Size limits of book releases per volume, by kind: e-books, illustrated editions and manga volumes. Book quality sizes count megabytes per volume, not per minute.
-- The maximums were tuned against observed releases (digital manga volumes of 340 to 630 MB); a person changes them under quality sizes.
INSERT INTO "QualityDefinitions" ("MediaTypeId", "Quality", "MinimumMbPerMinute", "MaximumMbPerMinute")
VALUES
    (3, 'Ebook', 0, 150),
    (3, 'Illustrated', 0, 300),
    (3, 'Manga', 0, 700)
ON CONFLICT ("MediaTypeId", "Quality") DO NOTHING;
