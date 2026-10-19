# Stop hook: when code changed since the last green run, build with warnings as errors and run the tests.
# Exit 2 sends Claude back to fix the failure before it stops. The load test is left to CI and /step-done.
try { [Console]::In.ReadToEnd() | Out-Null } catch { }

$root = git rev-parse --show-toplevel 2>$null
if (-not $root) { exit 0 }
Set-Location $root
if (-not (Test-Path 'src/Atlas.slnx')) { exit 0 }

# The "diff version": tracked changes plus the untracked files and their contents, hashed.
$untracked = @(git ls-files --others --exclude-standard)
$state = (git diff HEAD) -join "`n"
foreach ($file in $untracked) {
    if (Test-Path $file -PathType Leaf) { $state += "`n$file " + (Get-FileHash $file).Hash }
}
$sha = [Security.Cryptography.SHA256]::Create()
$hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($state))).Replace('-', '')

$marker = '.claude/.gate-checked'
if ((Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $hash)) { exit 0 }

$changed = @(git diff HEAD --name-only) + $untracked
if (-not ($changed | Where-Object { $_ -match '\.(cs|csproj|props|slnx|json)$' })) {
    Set-Content $marker $hash
    exit 0
}

$failures = '.claude/.gate-failures'
function Fail([string] $what, $output) {
    $count = 1
    if (Test-Path $failures) { $count = [int](Get-Content $failures -Raw) + 1 }
    Set-Content $failures $count
    if ($count -gt 3) {
        Remove-Item $failures
        [Console]::Error.WriteLine("build-gate: $what still failing after three rounds. Stopping; tell the user.")
        exit 0
    }
    $tail = ($output | Select-Object -Last 40) -join "`n"
    [Console]::Error.WriteLine("build-gate: $what failed. Fix it before stopping.`n$tail")
    exit 2
}

function Test-Port([int] $port) {
    $client = New-Object Net.Sockets.TcpClient
    try { return $client.ConnectAsync('127.0.0.1', $port).Wait(500) } catch { return $false } finally { $client.Dispose() }
}

$output = dotnet build src/Atlas.slnx -p:TreatWarningsAsErrors=true -nologo -v:q 2>&1
if ($LASTEXITCODE -ne 0) { Fail 'The build (warnings as errors)' $output }

$output = dotnet test src/Atlas.UnitTests --no-build -nologo -v:q 2>&1
if ($LASTEXITCODE -ne 0) { Fail 'The unit tests' $output }

$sqlPort = 1433
if ($env:SQL_PORT) { $sqlPort = [int]$env:SQL_PORT }
$blobPort = 10000
if ($env:BLOB_PORT) { $blobPort = [int]$env:BLOB_PORT }
if ((Test-Port $sqlPort) -and (Test-Port $blobPort)) {
    $output = dotnet test src/Atlas.IntegrationTests --no-build -nologo -v:q --filter 'Category!=Load' 2>&1
    if ($LASTEXITCODE -ne 0) { Fail 'The integration tests' $output }
}

if (Test-Path $failures) { Remove-Item $failures }
Set-Content $marker $hash
exit 0
