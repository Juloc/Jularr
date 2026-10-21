# Adds the owner's test books (D:\testlibrary\books, read only) as a library to a throwaway preview site and scans it, so they appear in Library and open in the Reader.
# Nothing in the folder is changed: the mount is read only and the scan only reads. Skipped when the folder cannot be read.
# Root is the preview's folder with owner-login.txt ("password: ..."); Db and Container name its PostgreSQL database and Docker container (user jularr).
# Example: .\preview-test-library.ps1 -Url http://localhost:5231 -Root D:\git\worktrees\preview-all -Db preview_all_2 -Container jularr-preview-db
param(
    [string]$Folder = 'D:\testlibrary\books',
    [Parameter(Mandatory)][string]$Url,
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Db,
    [Parameter(Mandatory)][string]$Container
)
$ErrorActionPreference = 'Stop'
try { $null = Get-ChildItem -LiteralPath $Folder -ErrorAction Stop | Select-Object -First 1 } catch { "skipped: $Folder cannot be read"; return }

function Scalar([string]$query) { (docker exec $Container psql -U jularr -d $Db -tAc $query) }
$parent = Split-Path -Parent $Folder
$relative = Split-Path -Leaf $Folder
if (-not (Scalar "select 1 from ""LibraryRoots"" where ""Name"" = 'Test books'")) {
    $sql = @"
DO `$`$
DECLARE mount_id bigint;
BEGIN
    INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('Test library', 3, '$($parent.Replace("'", "''"))', TRUE) RETURNING "Id" INTO mount_id;
    INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "StorageMountId", "RelativePath") VALUES ('Test books', 3, mount_id, '$($relative.Replace("'", "''"))');
END `$`$;
"@
    $sql | docker exec -i $Container psql -U jularr -d $Db -v ON_ERROR_STOP=1 -q
    'library root added'
}

# The scan is queued through the owner's own session, as the Admin page does.
$password = ((Get-Content "$Root\owner-login.txt" | Where-Object { $_ -like 'password:*' }) -replace '^password:\s*', '').Trim()
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new(); $script:token = $null
function Call([string]$Method, [string]$Path, $Body) {
    $headers = @{}; if ($script:token) { $headers.RequestVerificationToken = $script:token }
    $request = @{ Method = $Method; Uri = "$Url$Path"; WebSession = $session; Headers = $headers; UseBasicParsing = $true }
    if ($null -ne $Body) { $request.Body = ($Body | ConvertTo-Json -Depth 5); $request.ContentType = 'application/json' }
    $response = Invoke-WebRequest @request
    if ($response.Headers['RequestVerificationToken']) { $script:token = [string]$response.Headers['RequestVerificationToken'] }
    if ($response.Content) { return $response.Content | ConvertFrom-Json }
}
Call GET '/api/v1/auth' $null | Out-Null
Call POST '/api/v1/auth/login' @{ email = 'owner@example.test'; password = $password } | Out-Null
$session.Cookies.GetAllCookies() | Where-Object { $_.Secure } | ForEach-Object { $_.Secure = $false }
$rootId = Scalar "select ""Id"" from ""LibraryRoots"" where ""Name"" = 'Test books'"
Call POST "/api/v1/admin/library/roots/$rootId/scans" $null | Out-Null
'scan queued'
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 3
    $files = Scalar "select count(*) from ""StoredFiles"" where ""LibraryRootId"" = $rootId"
    $running = Scalar "select count(*) from ""Operations"" where ""OperationTypeId"" = 1 and ""OperationStatusTypeId"" in (1, 2)"
    if ([int]$files -gt 0 -and [int]$running -eq 0) { break }
}
"files stored: $files"

# No provider is asked in the preview, so nothing identifies the books: each folder's title (the author's name and the year left out) becomes a Work,
# as the Admin library page does with "assign", and every file of that folder is a version of it.
$works = @{}
$rows = (docker exec $Container psql -U jularr -d $Db -tAF '|' -c "select ""Id"", ""RelativePath"" from ""StoredFiles"" where ""LibraryRootId"" = $rootId and ""MediaAssetId"" is null order by ""RelativePath""") | Where-Object { $_ }
foreach ($row in $rows) {
    $id, $path = $row -split '\|', 2
    $parts = $path -split '[\\/]'
    $name = if ($parts.Length -ge 2) { $parts[-2] } else { [IO.Path]::GetFileNameWithoutExtension($parts[-1]) }
    $title = ($name -replace '\s*\(\d{4}\)\s*$', '')
    if ($title -match '^.+? - (.+)$') { $title = $Matches[1] }
    $key = $title.ToLowerInvariant()
    if ($works.ContainsKey($key)) {
        Call PUT "/api/v1/admin/library/files/$id/assignment" @{ workId = $works[$key]; title = $null; workUnitTypeId = $null; unitNumber = $null; parentUnitNumber = $null; gamePlatformId = $null } | Out-Null
    } else {
        Call PUT "/api/v1/admin/library/files/$id/assignment" @{ workId = $null; title = $title; workUnitTypeId = $null; unitNumber = $null; parentUnitNumber = $null; gamePlatformId = $null } | Out-Null
        $works[$key] = [long](Scalar "select max(w.""Id"") from ""Works"" w where w.""MediaTypeId"" = 3 and w.""CanonicalTitle"" = '$($title.Replace("'", "''"))'")
    }
}
"books in the library: $($works.Count) titles from $($rows.Count) files"
