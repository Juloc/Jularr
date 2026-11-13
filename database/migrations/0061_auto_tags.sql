-- Tags Jularr keeps itself on every title with a target: its media type, its original language, its genres and who requested it. The key names what a tag
-- stands for ("kind:anime", "language:ja", "genre:7", "requester:12"); the page shows it in the person's language. Its label is the key, so it never meets
-- a tag a person named (those cannot hold a colon). A person neither renames nor removes them; release profiles may ask for them like for any tag.
ALTER TABLE "Tags" ADD COLUMN "AutoKey" varchar(58) NULL CHECK ("AutoKey" ~ '^[a-z]+:[a-z0-9_-]+$');
ALTER TABLE "Tags" DROP CONSTRAINT "Tags_Label_check";
ALTER TABLE "Tags" ADD CONSTRAINT "Tags_Label_check" CHECK (("AutoKey" IS NULL AND "Label" ~ '^[a-z0-9][a-z0-9 _-]*$') OR ("AutoKey" IS NOT NULL AND "Label" = 'auto:' || "AutoKey"));
CREATE UNIQUE INDEX "UX_Tags_AutoKey" ON "Tags" ("AutoKey") WHERE "AutoKey" IS NOT NULL;
