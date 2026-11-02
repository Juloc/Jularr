-- Smart Pages: a scanned book's pages in their own layout with the text set anew, as a third reading view. No baseline edits.
INSERT INTO "ReadingViewTypes" ("Id", "Key") VALUES (3, 'SmartPages') ON CONFLICT ("Id") DO NOTHING;
