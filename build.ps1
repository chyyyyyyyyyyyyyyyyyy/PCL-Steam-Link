<#
    Build script for "PCL Steam Link mod" (PCL Steam 联机修改版).

    Requirements:
      * .NET SDK 8.0+            (to publish the PCLink module)
      * Visual Studio 2022 Build Tools with the ".NET desktop development" workload
        (MSBuild + VB.NET/WPF targets, used for the launcher itself)

    Usage:
      powershell -ExecutionPolicy Bypass -File build.ps1
      powershell -ExecutionPolicy Bypass -File build.ps1 -SkipLink   # reuse existing Resources\PCLink.exe

    Output:
      Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe   (single-file launcher)
      out-pclink\PCLink.exe                                   (standalone link module)
#>
param(
    [ValidateSet('Release', 'Debug', 'Snapshot')]
    [string]$Configuration = 'Release',
    [switch]$SkipLink
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    $dotnet = (Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { throw 'dotnet.exe not found. Install the .NET SDK 8.0+ and retry.' }
    Write-Host "[1/3] dotnet SDK found: $dotnet"

    if (-not $SkipLink) {
        Write-Host '[2/3] Publishing PCLink (self-contained single-file) ...'
        & $dotnet publish (Join-Path $root 'PCLink\PCLink.csproj') -c Release -o (Join-Path $root 'out-pclink')
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

        $built = Join-Path $root 'out-pclink\PCLink.exe'
        $target = Join-Path $root 'Plain Craft Launcher 2\Resources\PCLink.exe'
        if (-not (Test-Path $built)) { throw "PCLink.exe was not produced at $built" }
        Copy-Item $built $target -Force
        Write-Host ("      embedded resource: {0} ({1:N0} bytes)" -f $target, (Get-Item $target).Length)
    }
    else {
        Write-Host '[2/3] Skipped PCLink build (using existing Resources\PCLink.exe)'
        if (-not (Test-Path (Join-Path $root 'Plain Craft Launcher 2\Resources\PCLink.exe'))) {
            throw 'Plain Craft Launcher 2\Resources\PCLink.exe is missing; run without -SkipLink.'
        }
    }

    Write-Host "[3/3] Building the launcher ($Configuration) ..."

    # Visual Studio MSBuild 需要知道 .NET SDK 在哪，才能还原 SDK 风格的项目
    # (PCLCS / MeloongCore 用的是 Microsoft.NET.Sdk)
    $sdkRoot = (& $dotnet --info) | Select-String -Pattern 'Base Path:' | ForEach-Object { ($_ -split 'Base Path:')[1].Trim() } | Select-Object -First 1
    if ($sdkRoot) {
        $env:DOTNET_HOST_PATH = $dotnet
        $env:MSBuildSDKsPath = Join-Path $sdkRoot 'Sdks'
        Write-Host "      MSBuildSDKsPath: $env:MSBuildSDKsPath"
    }
    & $dotnet restore (Join-Path $root 'PCLCS\PCLCS.csproj') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

    $msbuild = $null
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $candidate = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($candidate) { $msbuild = $candidate }
    }
    if (-not $msbuild) {
        $fallback = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
        if (Test-Path $fallback) { $msbuild = $fallback }
    }
    if (-not $msbuild) {
        throw 'MSBuild.exe not found. Install Visual Studio 2022 Build Tools (".NET desktop development").'
    }
    Write-Host "      MSBuild: $msbuild"

    & $msbuild (Join-Path $root 'Plain Craft Launcher 2.sln') `
        "/p:Configuration=$Configuration" `
        '/p:MSBuildEnableWorkloadResolver=false' `
        '/m' '/v:m' '/nologo'
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }

    $exe = Join-Path $root 'Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe'
    if (Test-Path $exe) {
        Write-Host ''
        Write-Host ("DONE -> {0} ({1:N0} bytes)" -f $exe, (Get-Item $exe).Length)
    }
    else {
        throw "Build finished but $exe was not found."
    }
}
finally {
    Pop-Location
}
