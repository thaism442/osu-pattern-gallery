@echo off
rem Makes a ready-to-use package for other mappers (no .NET needed on their computer).
cd /d "%~dp0"
if exist Release rmdir /s /q Release
echo Building release package...
dotnet publish PatternGallery.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "%~dp0Release\PatternGallery"
if errorlevel 1 (echo. & echo BUILD FAILED - see the error above. & pause & exit /b 1)
copy /y README.txt "Release\PatternGallery\README.txt" >nul
powershell -NoProfile -Command "Compress-Archive -Path 'Release\PatternGallery\*' -DestinationPath 'Release\PatternGallery-win64.zip' -Force"
echo.
echo Done! Share this file: Release\PatternGallery-win64.zip
explorer "%~dp0Release"
pause
