<#
.SYNOPSIS
    Starts Atlas: the compose stack (SQL Server, Seq, Azurite), then the three services.

.DESCRIPTION
    Ctrl+C stops the services. The containers keep running; stop them with
    `docker compose -f platform/docker-compose.yml down`.
    Honours SQL_PORT, SEQ_PORT and BLOB_PORT, like the compose file.

    Windows blocks scripts by default, so run it as:
        powershell -ExecutionPolicy Bypass -File scripts\run.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host 'Starting SQL Server, Seq and Azurite...'
docker compose -f (Join-Path $root 'platform/docker-compose.yml') up -d --wait
if ($LASTEXITCODE -ne 0) { throw 'docker compose failed. Is Docker running?' }

# Build once: three `dotnet run` builds in parallel would fight over Atlas.Shared.
Write-Host 'Building...'
dotnet build (Join-Path $root 'src/Atlas.slnx') -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }

$services = @(
    @{ Name = 'Atlas.Simulators'; Url = 'http://localhost:5300' },
    @{ Name = 'Atlas.Screening'; Url = 'http://localhost:5200' },
    @{ Name = 'Atlas.Onboarding'; Url = 'http://localhost:5100' }
)
$seqPort = 5341
if ($env:SEQ_PORT) { $seqPort = $env:SEQ_PORT }

$processes = @()
try {
    foreach ($service in $services) {
        $project = Join-Path $root "src/$($service.Name)"
        $processes += Start-Process dotnet -NoNewWindow -PassThru `
            -ArgumentList "run --no-build --launch-profile http --project `"$project`""
    }

    foreach ($service in $services) {
        $deadline = (Get-Date).AddMinutes(2)
        while ($true) {
            try {
                $response = Invoke-WebRequest "$($service.Url)/health/ready" -UseBasicParsing -TimeoutSec 5
                if ($response.StatusCode -eq 200) { break }
            } catch { }
            if ((Get-Date) -gt $deadline) { throw "$($service.Name) didn't become ready at $($service.Url)." }
            Start-Sleep -Seconds 1
        }
    }

    Write-Host ''
    Write-Host 'Atlas is running. Ctrl+C stops the services.'
    Write-Host '  Onboarding  http://localhost:5100/swagger'
    Write-Host '  Screening   http://localhost:5200/swagger'
    Write-Host '  Simulators  http://localhost:5300/swagger'
    Write-Host "  Seq         http://localhost:$seqPort"
    Wait-Process -Id ($processes | ForEach-Object { $_.Id })
}
finally {
    $ErrorActionPreference = 'SilentlyContinue'
    foreach ($process in $processes) {
        if (-not $process.HasExited) { taskkill /PID $process.Id /T /F | Out-Null }
    }
}
