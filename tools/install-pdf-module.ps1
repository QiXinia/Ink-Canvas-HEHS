param(
    [Parameter(Mandatory)][string]$ApplicationDirectory,
    [string]$Package = 'artifacts\pdf-package\inkcanvas.pdf-1.0.0.zip',
    [switch]$RegisterOpenWith
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appRoot = [IO.Path]::GetFullPath($ApplicationDirectory)
$exe = Join-Path $appRoot 'InkCanvas.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'InkCanvas.exe was not found in the application directory.' }
$running = Get-Process -Name InkCanvas -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) { throw 'Exit this copy of Ink Canvas before installing its native PDF module.' }
$zipPath = if ([IO.Path]::IsPathRooted($Package)) { [IO.Path]::GetFullPath($Package) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $Package)) }
$modules = Join-Path $appRoot 'Modules'
New-Item -ItemType Directory -Path $modules -Force | Out-Null
if ((Get-Item -LiteralPath $modules).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The Modules directory must be a regular directory.' }
$target = Join-Path $modules 'inkcanvas.pdf'
$identifier = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $modules ('.pdf-install-' + $identifier)
$backup = Join-Path $modules ('.pdf-previous-' + $identifier)
New-Item -ItemType Directory -Path $stage | Out-Null
$installed = $false
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if ($archive.Entries.Count -gt 256 -or ($archive.Entries | Measure-Object -Property Length -Sum).Sum -gt 128MB) { throw 'The PDF module archive is unexpectedly large.' }
        foreach ($entry in $archive.Entries) {
            $destination = [IO.Path]::GetFullPath((Join-Path $stage $entry.FullName))
            if (-not $destination.StartsWith($stage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive entry escapes the installation staging directory.' }
        }
    } finally { $archive.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $stage)
    $payload = Join-Path $stage 'inkcanvas.pdf'
    $manifest = Get-Content -LiteralPath (Join-Path $payload 'module.json') -Raw | ConvertFrom-Json
    if ($manifest.Id -ne 'inkcanvas.pdf' -or $manifest.ApiVersion -ne 1 -or $manifest.EntryAssembly -ne 'InkCanvas.Modules.Pdf.dll') { throw 'This is not a compatible Ink Canvas PDF module.' }
    $required = @('module.json', $manifest.EntryAssembly, 'InkCanvas.Modules.Pdf.deps.json') + @($manifest.PackageFiles)
    foreach ($relative in $required) {
        $file = [IO.Path]::GetFullPath((Join-Path $payload $relative))
        if (-not $file.StartsWith($payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $file -PathType Leaf)) { throw ('Missing or invalid PDF dependency ' + $relative) }
    }
    if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $backup }
    try { Move-Item -LiteralPath $payload -Destination $target; $installed = $true }
    catch { if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $backup -Destination $target }; throw }
    if ($RegisterOpenWith) {
        $classes = 'HKCU:\Software\Classes'
        $program = Join-Path $classes 'InkCanvas.Pdf'
        New-Item -Path ($program + '\shell\open\command') -Force | Out-Null
        Set-Item -LiteralPath $program -Value 'Ink Canvas PDF'
        Set-Item -LiteralPath ($program + '\shell\open\command') -Value ('"' + $exe + '" "%1"')
        New-Item -Path ($classes + '\.pdf\OpenWithProgids') -Force | Out-Null
        New-ItemProperty -LiteralPath ($classes + '\.pdf\OpenWithProgids') -Name 'InkCanvas.Pdf' -PropertyType String -Value '' -Force | Out-Null
    }
    Write-Output ('Installed PDF module at ' + $target)
    Write-Output 'Restart Ink Canvas. PDF commands are available from the main toolbar More menu.'
} finally {
    # These two paths were created by this invocation under the verified Modules directory.
    foreach ($temporary in @($stage, $backup)) {
        $absolute = [IO.Path]::GetFullPath($temporary)
        if ($absolute.StartsWith($modules + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $absolute)) {
            if ($absolute -eq $stage -or $installed) { Remove-Item -LiteralPath $absolute -Recurse -Force }
        }
    }
}
