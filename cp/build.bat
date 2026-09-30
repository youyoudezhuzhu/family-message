@echo off
REM ── 编译家庭消息远程解锁 Credential Provider ─────────────────────────────
REM 必须在 vcvars64 环境里跑（CI 里由 workflow 先 call vcvars64.bat）。
REM 产物：cp\out\FamilyAgentCp.dll（x64，无运行时依赖）
setlocal enabledelayedexpansion
set HERE=%~dp0
set OUT=%HERE%out
if not exist "%OUT%" mkdir "%OUT%"

echo [build] cl.exe 版本：
cl 2>&1 | findstr /C:"Version"

cl /nologo /LD /O2 /W4 /GS /EHsc /utf-8 /DUNICODE /D_UNICODE ^
   /Fo"%OUT%\\" /Fd"%OUT%\\FamilyAgentCp.pdb" ^
   "%HERE%src\FamilyAgentCp.cpp" ^
   /link /DEF:"%HERE%src\FamilyAgentCp.def" /OUT:"%OUT%\FamilyAgentCp.dll" ^
   ole32.lib secur32.lib crypt32.lib wtsapi32.lib shlwapi.lib advapi32.lib user32.lib
if errorlevel 1 (
  echo [build] ✗ 编译失败
  exit /b 1
)

echo [build] ✓ 产物：
dir /b "%OUT%\FamilyAgentCp.dll"
endlocal
