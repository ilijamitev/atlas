# PostToolUse hook: keep an edited C# file formatted. Whitespace only, so it is fast and needs no build.
# Formatting must never block an edit, so every failure exits 0.
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $path = $payload.tool_input.file_path
    if (-not $path -or -not $path.EndsWith('.cs') -or -not (Test-Path $path)) { exit 0 }

    $root = git rev-parse --show-toplevel 2>$null
    if (-not $root) { exit 0 }
    Set-Location $root
    $relative = Resolve-Path -Relative $path

    dotnet format whitespace . --folder --include $relative --verbosity quiet 2>&1 | Out-Null
} catch {
    exit 0
}
exit 0
