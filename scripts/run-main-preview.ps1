param(
    [Parameter(Mandatory)][string]$RootDirectory,
    [int]$HttpPort = 5241,
    [int]$HttpsPort = 7241,
    [string]$LanAddress,
    [string]$CertificateSubject,
    [string]$DotNetPath = 'dotnet',
    [string]$GitPath = 'git',
    [string]$NpmPath = 'npm.cmd',
    [string]$DockerPath = 'docker'
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$checkout = Join-Path $RootDirectory 'checkout'
$trustedCheckout = $checkout.Replace('\', '/')
$releases = Join-Path $RootDirectory 'releases'
$logs = Join-Path $RootDirectory 'logs'
$stopRequest = Join-Path $RootDirectory 'stop.request'
$schemaMarker = Join-Path $RootDirectory 'schema.sha256'
$settingsPath = Join-Path $RootDirectory 'preview.settings.json'
$baseUrl = "https://localhost:$HttpsPort"
$mutex = [System.Threading.Mutex]::new($false, 'Local\JularrMainPreview')
$app = $null
$runningCommit = $null
$runningDirectory = $null

# Fresh baselines in installation order; unknown files stop the update.
$baselines = @('Account', 'Instance', 'Media', 'Acquisition', 'Storage', 'Library', 'Playback')

function Write-Log([string]$Message)
{
    "$(Get-Date -Format o) $Message" | Tee-Object -FilePath (Join-Path $logs 'controller.log') -Append
}

function Get-SchemaHash
{
    $database = Join-Path $checkout 'database'
    $unknown = Get-ChildItem $database -Filter '*.sql' | Where-Object { $baselines -notcontains $_.BaseName }
    if ($unknown) { throw "Unknown baseline $($unknown[0].Name); update the runner's installation order." }

    $content = ($baselines | ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $database "$_.sql") }) -join "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($content)
    return [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-', '')
}

function Invoke-Psql([string]$Database, [string]$Sql)
{
    $Sql | & $DockerPath exec -i $settings.Database.Container psql -U $settings.Database.User -d $Database -v ON_ERROR_STOP=1 -q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Database command failed on $Database." }
}

function Reset-PreviewDatabase
{
    # Only the dedicated preview database is replaced.
    & $DockerPath start $settings.Database.Container | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The preview database container is unavailable.' }
    $name = $settings.Database.Name
    Invoke-Psql 'postgres' "DROP DATABASE IF EXISTS `"$name`" WITH (FORCE);"
    Invoke-Psql 'postgres' "CREATE DATABASE `"$name`";"
    foreach ($baseline in $baselines)
    {
        Invoke-Psql $name (Get-Content -Raw -LiteralPath (Join-Path $checkout "database/$baseline.sql"))
    }
}

function Invoke-Preview([string]$Method, [string]$Path, $Body, $Session)
{
    $headers = @{}
    if ($script:antiforgery) { $headers.RequestVerificationToken = $script:antiforgery }
    $request = @{ Method = $Method; Uri = "$baseUrl$Path"; WebSession = $Session; Headers = $headers; UseBasicParsing = $true }
    if ($null -ne $Body) { $request.Body = ($Body | ConvertTo-Json -Depth 5); $request.ContentType = 'application/json' }
    $response = Invoke-WebRequest @request
    if ($response.Headers['RequestVerificationToken']) { $script:antiforgery = [string]$response.Headers['RequestVerificationToken'] }
    if ($response.Content) { return $response.Content | ConvertFrom-Json }
}

function Initialize-PreviewData
{
    # Idempotent: Owner, test library and first scans through the public services.
    $seed = $settings.Seed
    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $script:antiforgery = $null
    Invoke-Preview 'GET' '/api/v1/auth' $null $session | Out-Null
    if ((Invoke-Preview 'GET' '/api/v1/setup' $null $session).requiresOwner)
    {
        $english = (Invoke-Preview 'GET' '/api/v1/localization/locales' $null $session) | Where-Object { $_.locale -eq 'en' } | Select-Object -First 1
        Invoke-Preview 'POST' '/api/v1/setup/owner' @{ email = $seed.OwnerEmail; displayName = $seed.OwnerName; password = $seed.OwnerPassword; passwordConfirmation = $seed.OwnerPassword; uiLocaleId = $english.id } $session | Out-Null
    }
    else
    {
        Invoke-Preview 'POST' '/api/v1/auth/login' @{ email = $seed.OwnerEmail; password = $seed.OwnerPassword } $session | Out-Null
    }

    $mounts = (Invoke-Preview 'GET' '/api/v1/admin/storage/mounts?pageSize=100' $null $session).items
    $mount = $mounts | Where-Object { $_.sharePath -eq $seed.LocalPath } | Select-Object -First 1
    if (-not $mount)
    {
        $mount = Invoke-Preview 'POST' '/api/v1/admin/storage/mounts' @{
            name = $seed.LocalName; storageProtocolTypeId = 3; host = $null; sharePath = $seed.LocalPath; protocolVersion = $null; username = $null; domain = $null
            isReadOnly = $true; password = $null; wakeMacAddress = $null; wakeBroadcastAddress = $null; wakeOnDemand = $false
        } $session
    }

    $roots = (Invoke-Preview 'GET' "/api/v1/admin/library/roots?storageMountId=$($mount.id)&pageSize=100" $null $session).items
    foreach ($root in $seed.LibraryRoots | Where-Object { $roots.relativePath -notcontains $_.RelativePath })
    {
        $created = Invoke-Preview 'POST' '/api/v1/admin/library/roots' @{ name = $root.Name; mediaTypeId = $root.MediaTypeId; storageMountId = $mount.id; relativePath = $root.RelativePath } $session
        Invoke-Preview 'POST' "/api/v1/admin/library/roots/$($created.id)/scans" $null $session | Out-Null
    }
}

if (-not $mutex.WaitOne(0)) { exit }

try
{
    # Runtime directories and local, uncommitted settings.
    New-Item -ItemType Directory -Path $releases, $logs -Force | Out-Null
    $PID | Set-Content (Join-Path $RootDirectory 'controller.pid')
    $settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
    foreach ($variable in $settings.Environment.PSObject.Properties)
    {
        [Environment]::SetEnvironmentVariable($variable.Name, [string]$variable.Value, 'Process')
    }

    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = "http://localhost:$HttpPort;https://localhost:$HttpsPort"
    if ($LanAddress)
    {
        # Bind only the selected interface.
        $address = [System.Net.IPAddress]::Parse($LanAddress)
        $octets = $address.GetAddressBytes()
        $privateAddress = $octets[0] -eq 10 -or ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) -or ($octets[0] -eq 192 -and $octets[1] -eq 168)
        if ($address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or -not $privateAddress)
        {
            throw 'LanAddress must be a specific private IPv4 address.'
        }
        if (-not $CertificateSubject) { throw 'LAN HTTPS requires a certificate covering localhost and the LAN address.' }
        $env:ASPNETCORE_URLS += ";https://${LanAddress}:$HttpsPort"
    }
    if ($CertificateSubject)
    {
        # Private key stays in Windows.
        $env:Kestrel__Certificates__Default__Subject = $CertificateSubject
        $env:Kestrel__Certificates__Default__Store = 'My'
        $env:Kestrel__Certificates__Default__Location = 'CurrentUser'
        $env:Kestrel__Certificates__Default__AllowInvalid = 'false'
    }
    $env:ASPNETCORE_HTTPS_PORT = "$HttpsPort"
    $env:DOTNET_NOLOGO = '1'
    $env:GIT_TERMINAL_PROMPT = '0'
    $env:GCM_INTERACTIVE = 'never'

    while (-not (Test-Path -LiteralPath $stopRequest))
    {
        try
        {
            # Preserve checkout changes
            $changes = & $GitPath -c "safe.directory=$trustedCheckout" -C $checkout status --porcelain
            if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect preview checkout.' }
            if ($changes) { throw 'Preview checkout has local changes; automatic update refused.' }

            & $GitPath -c "safe.directory=$trustedCheckout" -C $checkout fetch --quiet origin main
            if ($LASTEXITCODE -ne 0) { throw 'Cannot fetch main.' }
            & $GitPath -c "safe.directory=$trustedCheckout" -C $checkout merge --ff-only --quiet origin/main
            if ($LASTEXITCODE -ne 0) { throw 'main is not a fast-forward; manual review required.' }
            $commit = (& $GitPath -c "safe.directory=$trustedCheckout" -C $checkout rev-parse HEAD).Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve preview commit.' }

            if ($commit -ne $runningCommit -or $null -eq $app -or $app.HasExited)
            {
                # Build before replacing
                $release = Join-Path $releases $commit
                $buildLog = Join-Path $logs "build-$commit.log"
                Push-Location (Join-Path $checkout 'src/Jularr.Frontend')
                try
                {
                    # Compile fingerprinted assets.
                    & $NpmPath ci --no-audit --no-fund *> $buildLog
                    if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed; no running host was replaced.' }
                    & $NpmPath run build *>> $buildLog
                    if ($LASTEXITCODE -ne 0) { throw 'Frontend asset build failed; no running host was replaced.' }
                }
                finally { Pop-Location }

                & $DotNetPath publish (Join-Path $checkout 'src/Jularr.Frontend/Jularr.Frontend.csproj') --configuration Release --output $release *>> $buildLog
                if ($LASTEXITCODE -ne 0) { throw 'Publish failed; no running host was replaced.' }
                if (Test-Path -LiteralPath $stopRequest) { break }

                if ($null -ne $app -and -not $app.HasExited)
                {
                    $app.Kill($true)
                    $app.WaitForExit()
                }

                # Changed baselines rebuild the preview database.
                $schema = Get-SchemaHash
                $initialized = Join-Path $RootDirectory 'schema.installed'
                if (-not (Test-Path -LiteralPath $initialized) -or (Get-Content -LiteralPath $initialized) -ne $schema)
                {
                    Remove-Item -LiteralPath $schemaMarker -ErrorAction SilentlyContinue
                    Reset-PreviewDatabase
                    $schema | Set-Content -LiteralPath $initialized
                    $runningDirectory = $null
                }

                # Start the single host
                $startOptions = @{
                    FilePath = $DotNetPath
                    ArgumentList = '"' + (Join-Path $release 'Jularr.Frontend.dll') + '"'
                    WorkingDirectory = $release
                    WindowStyle = 'Hidden'
                    PassThru = $true
                    RedirectStandardOutput = Join-Path $logs "host-$commit.out.log"
                    RedirectStandardError = Join-Path $logs "host-$commit.err.log"
                }
                $app = Start-Process @startOptions

                # Check startup readiness
                $ready = $false
                for ($attempt = 0; $attempt -lt 30 -and -not $app.HasExited; $attempt++)
                {
                    try
                    {
                        $response = Invoke-WebRequest "$baseUrl/Account/Login" -TimeoutSec 2 -UseBasicParsing
                        $ready = $response.StatusCode -eq 200
                        if ($ready) { break }
                    }
                    catch { Start-Sleep -Seconds 1 }
                }

                if (-not $ready)
                {
                    if (-not $app.HasExited) { $app.Kill($true); $app.WaitForExit() }

                    # Restore the previous host
                    if ($runningDirectory)
                    {
                        $startOptions.ArgumentList = '"' + (Join-Path $runningDirectory 'Jularr.Frontend.dll') + '"'
                        $startOptions.WorkingDirectory = $runningDirectory
                        $startOptions.RedirectStandardOutput = Join-Path $logs "host-$runningCommit.out.log"
                        $startOptions.RedirectStandardError = Join-Path $logs "host-$runningCommit.err.log"
                        $app = Start-Process @startOptions
                    }

                    throw 'New host failed its startup check; inspect the host logs.'
                }

                $runningCommit = $commit
                $runningDirectory = $release
                @{
                    Commit = $commit
                    Url = $baseUrl
                    LanUrl = if ($LanAddress) { "https://${LanAddress}:$HttpsPort" } else { $null }
                    ProcessId = $app.Id
                    StartedAt = [DateTimeOffset]::UtcNow.ToString('O')
                } | ConvertTo-Json | Set-Content (Join-Path $RootDirectory 'status.json')
                Write-Log "Running main $commit"
            }

            # Setup repeats until it succeeded for this schema.
            if ($null -ne $app -and -not $app.HasExited -and -not (Test-Path -LiteralPath $schemaMarker))
            {
                Initialize-PreviewData
                Get-SchemaHash | Set-Content -LiteralPath $schemaMarker
                Write-Log "Preview data initialized for main $runningCommit"
            }
        }
        catch
        {
            Write-Log $_.Exception.Message
        }

        # Observe stop requests promptly
        for ($second = 0; $second -lt 60 -and -not (Test-Path -LiteralPath $stopRequest); $second++)
        {
            Start-Sleep -Seconds 1
        }
    }
}
finally
{
    if ($null -ne $app -and -not $app.HasExited) { $app.Kill($true); $app.WaitForExit() }
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
