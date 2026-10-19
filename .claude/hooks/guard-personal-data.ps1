# PostToolUse hook: flag log calls that could write personal data (GC-2026-0814 #1, and "no personal data
# in logs"). It looks at each logging statement in an edited C# file and exits 2 (feedback to Claude) when
# the statement destructures an object or names a personal field. It reviews Claude's edits; it is not a
# runtime control.
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $path = $payload.tool_input.file_path
    if (-not $path -or -not $path.EndsWith('.cs') -or -not (Test-Path $path)) { exit 0 }
    $source = Get-Content $path -Raw
    if (-not $source) { exit 0 }
} catch {
    exit 0
}

$logCall = '\b(?:_?[lL]ogger|Log|log)\.(?:Log)?(?:Information|Warning|Error|Debug|Critical|Trace|Fatal|Verbose)\s*\((?<args>.*?)\);'
$personalField = '\b(?:FirstName|LastName|DateOfBirth|Identifier|NationalId|Email|Phone|Image|DocumentImage|SelfieImage)\b'

$problems = @()
foreach ($match in [regex]::Matches($source, $logCall, 'Singleline')) {
    $arguments = $match.Groups['args'].Value
    $line = ($source.Substring(0, $match.Index) -split "`n").Count
    if ($arguments -match '\{@') {
        $problems += "line ${line}: the log call destructures an object ({@...}), which can serialize personal data"
    }
    if ($arguments -match $personalField) {
        $problems += "line ${line}: the log call names a personal field ($($Matches[0]))"
    }
}

if ($problems.Count -gt 0) {
    [Console]::Error.WriteLine("guard-personal-data: $path`n  " + ($problems -join "`n  ") +
        "`nLog IDs, statuses and outcomes only. Personal data must not reach Seq.")
    exit 2
}
exit 0
