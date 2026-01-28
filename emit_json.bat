@echo off
setlocal

REM Change to the directory where this batch file is located
cd /d "%~dp0"

REM Build first
dotnet build -c Release -o bin\Release\net10.0

REM Run the compiled executable directly
set OUTDIR=results
set JSONFILE=%OUTDIR%\envelopes-batch.json

bin\Release\net10.0\EnvelopeOcr.exe --emit-json "%JSONFILE%"

endlocal