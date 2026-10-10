-- Service REQUEST_HISTORY: bounded, current-profile authorization before paging.
SELECT
    request."PublicId" AS "AcquisitionRequestId",
    work."PublicId" AS "WorkId",
    request."AcquisitionRequestStatusTypeId",
    operation."PublicId" AS "OperationId",
    request."CreatedAt"
FROM
    "Accounts" AS account
INNER JOIN
    "AccountProfiles" AS membership
        ON membership."AccountId" = account."Id"
        AND membership."ProfileId" = @ActiveProfileId
INNER JOIN
    "AcquisitionRequests" AS request
        ON request."ProfileId" = membership."ProfileId"
INNER JOIN
    "Works" AS work
        ON work."Id" = request."WorkId"
LEFT JOIN
    "Operations" AS operation
        ON operation."Id" = request."OperationId"
WHERE
    account."Id" = @ActorAccountId
    AND account."IsEnabled" = TRUE
    AND @PageSize BETWEEN 1 AND 100
    AND @Offset BETWEEN 0 AND 100000
ORDER BY
    request."CreatedAt" DESC,
    request."Id" DESC
LIMIT @PageSize
OFFSET @Offset;
