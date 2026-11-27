<#
.SYNOPSIS
    Check or install the Windows x64 runtime required by Ink Canvas.
.EXAMPLE
    .\tools\install-runtime.ps1 -CheckOnly
.EXAMPLE
    .\tools\install-runtime.ps1 -MsiPath .\InkCanvas-WinUI3-3.14.0-win-x64.msi
.EXAMPLE
    .\tools\install-runtime.ps1 -RuntimeInstallerPath .\dotnet-runtime-10.0.0-win-x64.exe
#>
[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [string]$RuntimeInstallerPath,
    [string]$MsiPath
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
    [Environment]::OSVersion.Version.Build -lt 17763 -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Ink Canvas requires 64-bit Windows 10 build 17763 or newer. Windows 11 x64 is recommended.'
}

if ($MsiPath) {
    $MsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
    if ([IO.Path]::GetExtension($MsiPath) -ne '.msi') { throw 'MsiPath must point to an MSI file.' }
}
if ($RuntimeInstallerPath) {
    $RuntimeInstallerPath = (Resolve-Path -LiteralPath $RuntimeInstallerPath).Path
    if ([IO.Path]::GetExtension($RuntimeInstallerPath) -ne '.exe') { throw 'RuntimeInstallerPath must point to a Microsoft .NET runtime installer.' }
}

function Find-Runtime {
    $runtimeRegistry = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $runtimeKey = $runtimeRegistry.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
        try { $runtimeRoot = if ($runtimeKey) { [string]$runtimeKey.GetValue('InstallLocation') } }
        finally { if ($runtimeKey) { $runtimeKey.Dispose() } }
    }
    finally { $runtimeRegistry.Dispose() }
    if (-not $runtimeRoot) {
        $programFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
        $runtimeRoot = Join-Path $programFiles64 'dotnet'
    }
    $runtimeHost = Join-Path $runtimeRoot 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $runtimeHost)) { return }
    $runtimeLines = & $runtimeHost --list-runtimes
    if ($LASTEXITCODE -ne 0) { throw 'The installed dotnet host could not list its runtimes.' }
    $runtimeLines | Where-Object { $_ -match '^Microsoft\.NETCore\.App 10\.0\.\d+\s' } | Select-Object -Last 1
}

$installedRuntime = Find-Runtime
if ($CheckOnly) {
    if (-not $installedRuntime) { throw '.NET 10 x64 runtime is missing. Run this script without -CheckOnly to install it.' }
    Write-Output ('Runtime ready: ' + $installedRuntime)
    return
}

if (-not $installedRuntime) {
    if ($RuntimeInstallerPath) {
        $runtimeSignature = Get-AuthenticodeSignature -LiteralPath $RuntimeInstallerPath
        if ($runtimeSignature.Status -ne 'Valid' -or $runtimeSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
            throw 'The offline runtime installer must have a valid Microsoft Authenticode signature.'
        }
        Write-Output 'Installing the supplied Microsoft runtime. Windows may request administrator approval.'
        $runtimeSetup = Start-Process -FilePath $RuntimeInstallerPath -ArgumentList '/install','/quiet','/norestart' -Verb RunAs -WindowStyle Hidden -PassThru -Wait
        if ($runtimeSetup.ExitCode -notin @(0, 3010)) { throw ('Runtime installation failed with code ' + $runtimeSetup.ExitCode) }
    }
    else {
        $runtimeWinget = Get-Command winget.exe -ErrorAction SilentlyContinue
        if (-not $runtimeWinget) {
            throw 'WinGet is unavailable. Install Microsoft App Installer, or pass the official .NET 10 x64 installer using -RuntimeInstallerPath. https://dotnet.microsoft.com/download/dotnet/10.0'
        }
        & $runtimeWinget.Source install --id Microsoft.DotNet.Runtime.10 --exact --architecture x64 --source winget --accept-package-agreements --accept-source-agreements --silent --disable-interactivity
        if ($LASTEXITCODE -ne 0) { throw ('WinGet runtime installation failed with code ' + $LASTEXITCODE) }
    }
    $installedRuntime = Find-Runtime
    if (-not $installedRuntime) { throw 'The runtime is not available after setup. Restart Windows if the installer requested it, then rerun this script.' }
}
Write-Output ('Runtime ready: ' + $installedRuntime)

if ($MsiPath) {
    $applicationSetup = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList ('/i "' + $MsiPath + '" /qn /norestart') -WindowStyle Hidden -PassThru -Wait
    if ($applicationSetup.ExitCode -notin @(0, 3010)) { throw ('Ink Canvas installation failed with code ' + $applicationSetup.ExitCode) }
    Write-Output 'Ink Canvas installed for the current user. Open Ink Canvas from the Start menu.'
}
