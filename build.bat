@echo off
cd /d "%~dp0"
echo === dotnet --list-sdks === > build.log
dotnet --list-sdks >> build.log 2>&1
echo === build === >> build.log
dotnet build purpleEdit.sln -c Release >> build.log 2>&1
echo EXITCODE %ERRORLEVEL% >> build.log
type build.log
pause
