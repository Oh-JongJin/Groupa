$env:PATH = "C:\Users\USER\AppData\Local\Microsoft\dotnet;$env:PATH"
$env:DOTNET_ROOT = "C:\Users\USER\AppData\Local\Microsoft\dotnet"

Write-Host "Building JumpListLauncher..." -ForegroundColor Cyan

dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true

if ($LASTEXITCODE -eq 0) {
    $publishDir = "bin\Release\net8.0-windows\win-x64\publish"
    
    # Copy config.json to publish directory
    Copy-Item "config.json" "$publishDir\config.json" -Force
    
    Write-Host ""
    Write-Host "Build successful!" -ForegroundColor Green
    Write-Host "Output: $publishDir\JumpListLauncher.exe" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "To use:" -ForegroundColor Cyan
    Write-Host "1. Copy JumpListLauncher.exe and config.json to your desired location"
    Write-Host "2. Edit config.json to add your apps"
    Write-Host "3. Right-click the .exe > Pin to taskbar"
    Write-Host "4. Click the taskbar icon to open the launcher"
} else {
    Write-Host "Build failed!" -ForegroundColor Red
}
