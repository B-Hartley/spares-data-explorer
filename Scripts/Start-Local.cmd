@echo off
setlocal
cd /d "%~dp0.."
set ASPNETCORE_ENVIRONMENT=Development
dotnet run --project ClickSpares.SparesExplorer.csproj --urls http://localhost:5080
