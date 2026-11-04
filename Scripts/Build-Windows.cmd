@echo off
setlocal
cd /d "%~dp0.."
dotnet publish ClickSpares.SparesExplorer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o Publish-IIS
if errorlevel 1 exit /b %errorlevel%
echo.
echo Build ready in Publish-IIS. Preserve the server's Data folder and appsettings.json.
