@echo off
rem Compila la release di Cinecore Player per Windows nella cartella Release (accanto a questo file).
rem Serve il .NET 9 SDK. I componenti (third-parties) e le icone (Assets\Shell) sono gia' qui dentro.
cd /d "%~dp0win-native\CinecoreWin"
dotnet publish -c Release -p:PublishDir="%~dp0Release\\"
if errorlevel 1 (echo. & echo Compilazione NON riuscita. & pause & exit /b 1)
echo. & echo Fatto: %~dp0Release\CinecorePlayer2026.exe
pause
