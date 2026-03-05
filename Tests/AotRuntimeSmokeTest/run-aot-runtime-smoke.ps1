param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release",
    [string]$NativeWindowsDllPath = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $scriptDir "AotRuntimeSmokeTest.csproj"

function Test-IsPortableExecutable([string]$path) {
    if (-not (Test-Path $path)) {
        return $false
    }

    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -gt 2) {
        $bytes = $bytes[0..1]
    }
    return $bytes.Length -ge 2 -and $bytes[0] -eq 0x4D -and $bytes[1] -eq 0x5A
}

function Resolve-WindowsNativeDll([string]$explicitPath, [string]$scriptDirPath) {
    if (-not [string]::IsNullOrWhiteSpace($explicitPath)) {
        if (-not (Test-IsPortableExecutable $explicitPath)) {
            throw "Provided -NativeWindowsDllPath is not a valid PE DLL: $explicitPath"
        }
        return (Resolve-Path $explicitPath).Path
    }

    $repoDll = Join-Path $scriptDirPath "..\..\csharp\runtimes\win-x64\native\rocksdb.dll"
    if (Test-IsPortableExecutable $repoDll) {
        return (Resolve-Path $repoDll).Path
    }

    $nugetRoot = Join-Path $env:USERPROFILE ".nuget\packages\rocksdb"
    if (-not (Test-Path $nugetRoot)) {
        return $null
    }

    $candidates = Get-ChildItem -Directory $nugetRoot |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "runtimes\win-x64\native\rocksdb.dll" } |
        Where-Object { Test-IsPortableExecutable $_ }

    return $candidates | Select-Object -First 1
}

Write-Host "Publishing Native AOT smoke test ($Configuration, $Rid)..."
dotnet publish $projectPath -c $Configuration -f net10.0 -r $Rid -p:PublishAot=true -p:TargetFrameworks=net10.0
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$publishDir = Join-Path $scriptDir "bin\$Configuration\net10.0\$Rid\publish"
$exeName = if ($Rid.StartsWith("win-")) { "AotRuntimeSmokeTest.exe" } else { "AotRuntimeSmokeTest" }
$exePath = Join-Path $publishDir $exeName

if (-not (Test-Path $exePath)) {
    Write-Error "Published executable not found: $exePath"
    exit 1
}

if ($Rid -eq "win-x64") {
    $nativeDir = Join-Path $publishDir "runtimes\win-x64\native"
    $targetDll = Join-Path $nativeDir "rocksdb.dll"
    $sourceDll = Resolve-WindowsNativeDll -explicitPath $NativeWindowsDllPath -scriptDirPath $scriptDir

    if ($null -eq $sourceDll) {
        Write-Error "Unable to locate a valid rocksdb.dll for win-x64. Provide one with -NativeWindowsDllPath."
        exit 1
    }

    if (-not (Test-Path $nativeDir)) {
        New-Item -ItemType Directory -Path $nativeDir -Force | Out-Null
    }

    Copy-Item -Force $sourceDll $targetDll
    Write-Host "Using native library: $sourceDll"
}

Write-Host "Running Native AOT smoke test executable..."
Push-Location $publishDir
try {
    & $exePath
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($exitCode -ne 0) {
    Write-Error "Native AOT smoke test failed with exit code $exitCode"
    exit $exitCode
}

Write-Host "Native AOT smoke test passed."
exit 0
