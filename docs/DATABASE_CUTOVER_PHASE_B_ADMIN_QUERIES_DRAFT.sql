WITH "PageRoots" AS MATERIALIZED (
    SELECT
        account_group."Id",
        account_group."PublicId",
        account_group."Name",
        account_group."CreatedAt"
    FROM "AccountGroups" AS account_group
    WHERE EXISTS (
        SELECT
            1
        FROM "Accounts" AS actor
        INNER JOIN "AccountRoleTypes" AS role ON role."Id" = actor."AccountRoleTypeId"
        WHERE actor."Id" = @ActorAccountId
            AND actor."IsEnabled" = TRUE
            AND role."Key" = 'owner'
    )
    ORDER BY account_group."Name", account_group."Id"
    LIMIT @PageSize
    OFFSET @Offset
)
SELECT
    page."PublicId" AS "Id",
    page."Name"::text AS "Name",
    membership."MemberCount",
    page."CreatedAt"
FROM "PageRoots" AS page
CROSS JOIN LATERAL (
    SELECT
        COUNT(*) AS "MemberCount"
    FROM "AccountGroupAccounts" AS member
    WHERE member."AccountGroupId" = page."Id"
) AS membership
ORDER BY page."Name", page."Id";
