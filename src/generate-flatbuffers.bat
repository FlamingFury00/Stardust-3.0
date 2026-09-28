@echo off

cd /D "%~dp0"

echo Generating flatbuffers header file...

.\flatbuffers-schema\binaries\flatc.exe --csharp --gen-all --gen-object-api --gen-onefile -o .\RLBot\Flat .\flatbuffers-schema\schema\rlbot.fbs
IF ERRORLEVEL 1 EXIT /B 1

IF EXIST .\RLBot\Flat\Flat.cs del .\RLBot\Flat\Flat.cs

REM the file produced is called rlbot_generated.cs, rename it to Flat.cs after removing the old one
ren .\RLBot\Flat\rlbot_generated.cs Flat.cs

REM CMD doesn't have native text replacement tools, use PowerShell
REM Replaces 'rlbot.flat' with 'RLBot.Flat'
powershell -NoProfile -NonInteractive -Command "$ErrorActionPreference = 'Stop'; (Get-Content .\RLBot\Flat\Flat.cs) -replace 'rlbot\.flat', 'RLBot.Flat' | Set-Content .\RLBot\Flat\Flat.cs"
IF ERRORLEVEL 1 EXIT /B 1

echo Done.
