# Build Release
Write-Host "Building Release..."
dotnet build "c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\NINA.Plugins.Fujifilm.sln" -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed"; exit 1 }

# Find Build Artifacts
$binPath = "c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\src\NINA.Plugins.Fujifilm\bin\x64\Release\net8.0-windows"
if (-not (Test-Path $binPath)) {
    # Fallback to standard Release if x64 not found
    $binPath = "c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\src\NINA.Plugins.Fujifilm\bin\Release\net8.0-windows"
}
$dll = Get-ChildItem -Path $binPath -Filter "NINA.Plugins.Fujifilm.dll" -Recurse | Select-Object -First 1
if ($null -eq $dll) { Write-Error "DLL not found"; exit 1 }
$sourceDir = $dll.DirectoryName
Write-Host "Found artifacts at: $sourceDir"

# Define Plugin Root
$pluginRoot = "$env:LOCALAPPDATA\NINA\Plugins"
if (-not (Test-Path $pluginRoot)) { Write-Error "NINA Plugins directory not found"; exit 1 }

# Get all version directories (and include root for good measure if user used it)
$dirs = Get-ChildItem -Path $pluginRoot -Directory
$targets = @($dirs.FullName)
$targets += $pluginRoot # Add root plugins dir as well

foreach ($targetBase in $targets) {
    $fujiDir = Join-Path $targetBase "Fujifilm"
    
    # Clean
    if (Test-Path $fujiDir) {
        Write-Host "Removing old version at: $fujiDir"
        Remove-Item -Path $fujiDir -Recurse -Force
    }

    # Deploy (only if it looks like a version dir or is the root and user wants it there)
    # The user specifically mentioned 3.0.0 and 3.1.2.
    # We will deploy to all found directories to be safe.
    
    Write-Host "Deploying to: $fujiDir"
    New-Item -ItemType Directory -Force -Path $fujiDir | Out-Null
    robocopy $sourceDir $fujiDir /E /IS /NFL /NDL /NJH /NJS
    
    # Verify
    $deployedDll = Join-Path $fujiDir "NINA.Plugins.Fujifilm.dll"
    if (Test-Path $deployedDll) {
        $ver = (Get-Item $deployedDll).VersionInfo.FileVersion
        Write-Host "Deployed to $targetBase : Version $ver"
    }
}
