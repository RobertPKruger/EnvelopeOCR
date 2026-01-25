@echo off
setlocal

REM Adjust paths as needed
set OUTDIR=output
set JSONFILE=%OUTDIR%\envelopes-batch.json

dotnet run -- --emit-json "%JSONFILE%"

endlocal