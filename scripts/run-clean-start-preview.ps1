param(
    [Parameter(Mandatory)][string]$RootDirectory,
    [int]$HttpPort = 5240,
    [int]$HttpsPort = 7240,
    [string]$DotNetPath = 'dotnet',
    [string]$GitPath = 'git'
)

$ErrorActionPreference = 'Stop'
$checkout = Join-Path $RootDirectory 'checkout'
$trustedCheckout = $checkout.Replace('\', '/')
$releases = Join-Path $RootDirectory 'releases'
$logs = Join-Path $RootDirectory 'logs'
$stopRequest = Join-Path $RootDirectory 'stop.request'
$mutex = [System.Threading.Mutex]::new($false, 'Local\JularrCleanStartPreview')
$app = $null
$runningCommit = $null
$runningDirectory = $null

if (-not $mutex.WaitOne(0)) { exit }

try
{
    # Runtime directories
    New-Item -ItemType Directory -Path $releases, $logs -Force | Out-Null
    $PID | Set-Content (Join-Path $RootDirectory 'controller.pid')
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = "http://localhost:$HttpPort;https://localhost:$HttpsPort"
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

            & $GitPath -c "safe.directory=$trustedCheckout" -C $checkout fetch --quiet origin clean-start
            if ($LASTEXITCODE -ne 0) { throw 'Cannot fetch clean-start.' }
            & $GitPath -c "safe.directory=$trustedCheckout" -C $checkout merge --ff-only --quiet origin/clean-start
            if ($LASTEXITCODE -ne 0) { throw 'clean-start is not a fast-forward; manual review required.' }
            $commit = (& $GitPath -c "safe.directory=$trustedCheckout" -C $checkout rev-parse HEAD).Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve preview commit.' }

            if ($commit -ne $runningCommit -or $null -eq $app -or $app.HasExited)
            {
                # Build before replacing
                $release = Join-Path $releases $commit
                & $DotNetPath publish (Join-Path $checkout 'src/Jularr.Frontend/Jularr.Frontend.csproj') --configuration Release --output $release *> (Join-Path $logs "build-$commit.log")
                if ($LASTEXITCODE -ne 0) { throw 'Publish failed; no running host was replaced.' }
                if (Test-Path -LiteralPath $stopRequest) { break }

                if ($null -ne $app -and -not $app.HasExited)
                {
                    $app.Kill($true)
                    $app.WaitForExit()
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
                        $response = Invoke-WebRequest "https://localhost:$HttpsPort/Account/Login" -SkipCertificateCheck -TimeoutSec 2
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
                    Url = "https://localhost:$HttpsPort"
                    ProcessId = $app.Id
                    StartedAt = [DateTimeOffset]::UtcNow.ToString('O')
                } | ConvertTo-Json | Set-Content (Join-Path $RootDirectory 'status.json')
                "$(Get-Date -Format o) Running clean-start $commit" | Tee-Object -FilePath (Join-Path $logs 'controller.log') -Append
            }
        }
        catch
        {
            "$(Get-Date -Format o) $($_.Exception.Message)" | Tee-Object -FilePath (Join-Path $logs 'controller.log') -Append
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
