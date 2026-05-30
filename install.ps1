# 1. Uninstalls the yamca global dotnet tool
# 2. Packs a new nuget package of Yamca.Web that can be installed as a global tool
# 3. Installs the package as a global tool

# This is for testing the user experience of a build on a local machine without
# having to publish a new tool version to nuget.

Write-Host "Uninstalling global tool yamca..." -ForegroundColor Yellow
dotnet tool uninstall -g "yamca" 2>$null

Write-Host "Packing tool..." -ForegroundColor Yellow
dotnet pack "Yamca.Web" -c Release

$pkg = Get-ChildItem "Yamca.Web/bin/Release" -Recurse -Filter "*.nupkg" |
       Sort-Object LastWriteTime -Descending |
       Select-Object -First 1

if (-not $pkg) {
    throw "No .nupkg found after packing."
}

Write-Host "Installing tool from $($pkg.FullName)..." -ForegroundColor Green
dotnet tool install -g yamca --add-source $pkg.Directory.FullName

Write-Host "Done."