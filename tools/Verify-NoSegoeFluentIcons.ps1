$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$applicationRoot = Join-Path $repositoryRoot 'Uniwill Control Utility'
$sourceFiles = Get-ChildItem -LiteralPath $applicationRoot -Recurse -File |
    Where-Object {
        $_.Extension -in @('.cs', '.xaml') -and
        $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
    }

$forbiddenReferences = @('Segoe Fluent Icons', 'FontIcon')
foreach ($forbiddenReference in $forbiddenReferences) {
    $references = Select-String -Path $sourceFiles.FullName -SimpleMatch $forbiddenReference
    if ($references.Count -gt 0) {
        $references | ForEach-Object {
            Write-Output "$($_.Path):$($_.LineNumber): $($_.Line.Trim())"
        }

        throw "Found $($references.Count) source references to '$forbiddenReference'."
    }
}

Write-Output 'PASS: application source uses SymbolIcon without legacy FontIcon references.'
