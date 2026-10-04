@echo off
cd /d "%~dp0"
echo Building osu! Pattern Gallery...
dotnet publish PatternGallery.csproj -c Release -o "%~dp0App"
if errorlevel 1 (echo. & echo BUILD FAILED - see the error above. & pause & exit /b 1)
echo.
echo Done! Open App\PatternGallery.exe
pause
