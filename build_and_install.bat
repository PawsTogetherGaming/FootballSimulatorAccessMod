@echo off
echo === Football Accessibility Mod - Build and Install ===
echo.

set GAME_PATH=C:\Program Files (x86)\Steam\steamapps\common\Football Simulator\
set PLUGIN_DIR=%GAME_PATH%BepInEx\plugins\

echo Building mod...
cd /d c:\football\FootballAccessMod
dotnet build -c Release
if errorlevel 1 (
    echo BUILD FAILED. Check errors above.
    pause
    exit /b 1
)

echo.
echo Installing to %PLUGIN_DIR%...
if not exist "%PLUGIN_DIR%" mkdir "%PLUGIN_DIR%"
copy /y "bin\Release\FootballAccessMod.dll" "%PLUGIN_DIR%"
if errorlevel 1 (
    echo INSTALL FAILED. Try running as Administrator.
    pause
    exit /b 1
)

echo.
echo SUCCESS! Mod installed.
echo Launch Football Simulator via Steam to use.
echo NVDA will speak: "Football Simulator accessibility mod loaded."
echo.
pause
