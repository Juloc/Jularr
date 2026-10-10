-- PHASE B WORKING DRAFT 10: cross-blueprint relational identity for Curriculum/Learner.
-- Run AFTER DRAFTS 01..09 on a fresh, isolated PostgreSQL test DB only.
-- No historical-data migration is intended; do not apply this to an active instance.
--
-- Rationale: a plain FK to CurriculumExercises(Id) proves existence but DOES NOT
-- prove that the referenced exercise is inside the learner's pinned blueprint.
-- The composite keys below make a learner's course, progress and attempts use
-- the SAME CurriculumBlueprintId, including through Level/Chapter/Lesson/Exercise.
-- Duplicated BlueprintId columns here are FK discriminators, not second owners
-- of curriculum state. PostgreSQL rejects mismatches without custom triggers.
BEGIN;

-- Carry the immutable blueprint discriminator through the curriculum hierarchy.
ALTER TABLE "CurriculumLevels"
    ADD CONSTRAINT "UX_CurriculumLevels_Id_Blueprint"
        UNIQUE ("Id","CurriculumBlueprintId");

ALTER TABLE "CurriculumChapters"
    ADD COLUMN "CurriculumBlueprintId" bigint NOT NULL,
    ADD CONSTRAINT "UX_CurriculumChapters_Id_Blueprint"
        UNIQUE ("Id","CurriculumBlueprintId"),
    ADD CONSTRAINT "FK_CurriculumChapters_LevelBlueprint"
        FOREIGN KEY ("CurriculumLevelId","CurriculumBlueprintId")
        REFERENCES "CurriculumLevels" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT;

ALTER TABLE "CurriculumLessons"
    ADD COLUMN "CurriculumBlueprintId" bigint NOT NULL,
    ADD CONSTRAINT "UX_CurriculumLessons_Id_Blueprint"
        UNIQUE ("Id","CurriculumBlueprintId"),
    ADD CONSTRAINT "FK_CurriculumLessons_ChapterBlueprint"
        FOREIGN KEY ("CurriculumChapterId","CurriculumBlueprintId")
        REFERENCES "CurriculumChapters" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT;

ALTER TABLE "CurriculumExercises"
    ADD COLUMN "CurriculumBlueprintId" bigint NOT NULL,
    ADD CONSTRAINT "UX_CurriculumExercises_Id_Blueprint"
        UNIQUE ("Id","CurriculumBlueprintId"),
    ADD CONSTRAINT "FK_CurriculumExercises_LessonBlueprint"
        FOREIGN KEY ("CurriculumLessonId","CurriculumBlueprintId")
        REFERENCES "CurriculumLessons" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT;

-- The published SharedCourse pins one immutable Blueprint version.
ALTER TABLE "SharedCourseInstances"
    ADD CONSTRAINT "UX_SharedCourseInstances_Id_Blueprint"
        UNIQUE ("Id","CurriculumBlueprintId");

ALTER TABLE "SharedCourseExerciseContent"
    ADD COLUMN "CurriculumBlueprintId" bigint NOT NULL,
    ADD CONSTRAINT "FK_SharedCourseExerciseContent_InstanceBlueprint"
        FOREIGN KEY ("SharedCourseInstanceId","CurriculumBlueprintId")
        REFERENCES "SharedCourseInstances" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_SharedCourseExerciseContent_ExerciseBlueprint"
        FOREIGN KEY ("CurriculumExerciseId","CurriculumBlueprintId")
        REFERENCES "CurriculumExercises" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT;

-- LearnerCourse's ProfileId is already bound by Profiles FK and its Id/ProfileId
-- unique constraint in part 05. This adds the pinned published blueprint.
ALTER TABLE "LearnerCourses"
    ADD COLUMN "CurriculumBlueprintId" bigint NOT NULL,
    ADD CONSTRAINT "UX_LearnerCourses_Id_Blueprint"
        UNIQUE ("Id","CurriculumBlueprintId"),
    ADD CONSTRAINT "FK_LearnerCourses_InstanceBlueprint"
        FOREIGN KEY ("SharedCourseInstanceId","CurriculumBlueprintId")
        REFERENCES "SharedCourseInstances" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT;

-- Every progress target belongs to the learner's pinned Blueprint.
-- The existing CHECK(num_nonnulls(...)=1) remains authoritative for target count.
ALTER TABLE "LearnerCourseProgress"
    ADD COLUMN "CurriculumBlueprintId" bigint NOT NULL,
    ADD CONSTRAINT "FK_LearnerCourseProgress_CourseBlueprint"
        FOREIGN KEY ("LearnerCourseId","CurriculumBlueprintId")
        REFERENCES "LearnerCourses" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_LearnerCourseProgress_LevelBlueprint"
        FOREIGN KEY ("CurriculumLevelId","CurriculumBlueprintId")
        REFERENCES "CurriculumLevels" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_LearnerCourseProgress_ChapterBlueprint"
        FOREIGN KEY ("CurriculumChapterId","CurriculumBlueprintId")
        REFERENCES "CurriculumChapters" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_LearnerCourseProgress_LessonBlueprint"
        FOREIGN KEY ("CurriculumLessonId","CurriculumBlueprintId")
        REFERENCES "CurriculumLessons" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_LearnerCourseProgress_ExerciseBlueprint"
        FOREIGN KEY ("CurriculumExerciseId","CurriculumBlueprintId")
        REFERENCES "CurriculumExercises" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT;

ALTER TABLE "LearnerExerciseAttempts"
    ADD COLUMN "CurriculumBlueprintId" bigint NOT NULL,
    ADD CONSTRAINT "FK_LearnerExerciseAttempts_CourseBlueprint"
        FOREIGN KEY ("LearnerCourseId","CurriculumBlueprintId")
        REFERENCES "LearnerCourses" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_LearnerExerciseAttempts_ExerciseBlueprint"
        FOREIGN KEY ("CurriculumExerciseId","CurriculumBlueprintId")
        REFERENCES "CurriculumExercises" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT;

-- Sessions may be unscoped or based on a private LearningCourse (FSRS).
-- If a Curriculum lesson is specified, it MUST be scoped to the matching
-- LearnerCourse's Blueprint; never accept a cross-course lesson silently.
ALTER TABLE "LearningActivitySessions"
    ADD COLUMN "CurriculumBlueprintId" bigint,
    ADD CONSTRAINT "CK_LearningActivitySessions_CurriculumBlueprintScope"
        CHECK (
            ("LearnerCourseId" IS NULL AND "CurriculumBlueprintId" IS NULL
             AND "CurriculumLessonId" IS NULL)
            OR
            ("LearnerCourseId" IS NOT NULL AND "CurriculumBlueprintId" IS NOT NULL
             AND "LearningCourseId" IS NULL)
        ),
    ADD CONSTRAINT "FK_LearningActivitySessions_LearnerCourseBlueprint"
        FOREIGN KEY ("LearnerCourseId","CurriculumBlueprintId")
        REFERENCES "LearnerCourses" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_LearningActivitySessions_LessonBlueprint"
        FOREIGN KEY ("CurriculumLessonId","CurriculumBlueprintId")
        REFERENCES "CurriculumLessons" ("Id","CurriculumBlueprintId")
        ON DELETE RESTRICT;

COMMIT;
