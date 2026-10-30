-- The Ollama machines that translate, in the Owner's order: the first that answers with an approved model works, the next takes over when it
-- drops out. Empty: the configured endpoint alone (`LocalTranslation:Endpoint`), so an upgraded install keeps translating as before.
ALTER TABLE "InstanceSettings" ADD COLUMN "TranslationMachines" varchar(255)[] NOT NULL DEFAULT '{}' CHECK (cardinality("TranslationMachines") <= 8);
