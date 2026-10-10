WITH "PageContexts" AS MATERIALIZED (
    SELECT
        context."Id",
        context."PublicId",
        context."LearningUnitId",
        context."WorkId",
        context."WorkEpisodeId",
        context."WorkChapterId",
        context."PositionKey",
        context."LanguageTag",
        context."SourceText",
        context."CreatedAt"
    FROM "LearningContexts" AS context
    WHERE context."ProfileId" = @ActiveProfileId
      AND context."LearningUnitId" = (
          SELECT unit."Id"
          FROM "LearningUnits" AS unit
          WHERE unit."PublicId" = @LearningUnitPublicId
      )
      AND EXISTS (
          SELECT 1
          FROM "AccountProfiles" AS membership
          JOIN "Accounts" AS actor ON actor."Id" = membership."AccountId"
          WHERE membership."AccountId" = @ActorAccountId
            AND membership."ProfileId" = @ActiveProfileId
            AND actor."IsEnabled" = true
      )
    ORDER BY context."CreatedAt", context."Id"
    LIMIT @PageSize
    OFFSET @Offset
)
SELECT
    context."PublicId" AS "Id",
    unit."PublicId" AS "LearningUnitId",
    work."PublicId" AS "WorkId",
    episode."PublicId" AS "WorkEpisodeId",
    chapter."PublicId" AS "WorkChapterId",
    context."PositionKey",
    context."LanguageTag",
    context."SourceText",
    context."CreatedAt"
FROM "PageContexts" AS context
JOIN "LearningUnits" AS unit ON unit."Id" = context."LearningUnitId"
JOIN "Works" AS work ON work."Id" = context."WorkId"
LEFT JOIN "WorkEpisodes" AS episode ON episode."Id" = context."WorkEpisodeId"
LEFT JOIN "WorkChapters" AS chapter ON chapter."Id" = context."WorkChapterId"
ORDER BY context."CreatedAt", context."Id";
