# -*- coding: utf-8 -*-
"""重新生成 tests/run_tests.cmd：纯 ASCII + CRLF 换行 + 全程日志 + 结束暂停（修复双击闪退）。"""
import os

CONTENT = r'''@echo off
rem ============================================================
rem  VP Tool Gray Update - one-click build + test script
rem  Usage: double-click, or run tests\run_tests.cmd from repo root
rem  Output: console + tests\test-run.log (persists if window closes)
rem ============================================================
setlocal
cd /d "%~dp0.."

set LOG=tests\test-run.log
echo [%DATE% %TIME%] run_tests started > %LOG%

set MSBUILD="C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"

if not exist %MSBUILD% (
    echo [ERROR] MSBuild not found at %MSBUILD%
    echo [ERROR] MSBuild not found at %MSBUILD% >> %LOG%
    goto :pause_fail
)

echo [1/5] Building main solution (Release)...
echo [1/5] Building main solution (Release)... >> %LOG%
%MSBUILD% MoveImageForm.sln /t:Build /p:Configuration=Release /m /v:m /nologo >> %LOG% 2>&1
if errorlevel 1 (
    echo [ERROR] Solution build failed. See %LOG%
    goto :pause_fail
)
echo     OK

echo [2/5] Building test projects...
echo [2/5] Building test projects... >> %LOG%
%MSBUILD% tests\FakeMainApp\FakeMainApp.csproj /t:Build /p:Configuration=Release /v:m /nologo >> %LOG% 2>&1
if errorlevel 1 (
    echo [ERROR] FakeMainApp build failed. See %LOG%
    goto :pause_fail
)
%MSBUILD% tests\UpdateSystemTests\UpdateSystemTests.csproj /t:Build /p:Configuration=Release /v:m /nologo >> %LOG% 2>&1
if errorlevel 1 (
    echo [ERROR] UpdateSystemTests build failed. See %LOG%
    goto :pause_fail
)
echo     OK

echo [3/5] Starting SFTP test server (127.0.0.1:2222)...
echo [3/5] SFTP server prep >> %LOG%
python -c "import asyncssh" 2>nul
if errorlevel 1 (
    echo     installing asyncssh...
    python -m pip install asyncssh -q >> %LOG% 2>&1
)
start "vp-sftp-test" /min python test_sftp\standalone_sftp_server.py
timeout /t 3 /nobreak >nul

echo [4/5] Running tests...
echo [4/5] Running tests... >> %LOG%
tests\UpdateSystemTests\bin\Release\UpdateSystemTests.exe --sftp --launcher out-bin\Launcher.exe --fakemain tests\FakeMainApp\bin\Release\MoveImageForm.exe
set TESTRC=%ERRORLEVEL%
echo test exit code: %TESTRC% >> %LOG%

echo [5/5] Cleanup...
for /f "tokens=5" %%a in ('netstat -ano ^| findstr ":2222" ^| findstr "LISTENING"') do taskkill /f /pid %%a >nul 2>&1
taskkill /f /im Launcher.exe >nul 2>&1
taskkill /f /im MoveImageForm.exe >nul 2>&1

echo.
if "%TESTRC%"=="0" (
    echo ==== ALL TESTS PASSED ====
    echo ALL TESTS PASSED >> %LOG%
) else (
    echo ==== %TESTRC% TEST(S) FAILED - see above and tests\UpdateSystemTests\test-results.log ====
    echo TESTS FAILED rc=%TESTRC% >> %LOG%
)
echo.
pause
exit /b %TESTRC%

:pause_fail
echo.
echo ==== FAILED - details in %LOG% ====
echo.
pause
exit /b 1
'''

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "run_tests.cmd")
data = CONTENT.replace("\n", "\r\n").encode("ascii")
with open(out, "wb") as f:
    f.write(data)
print("written:", out, len(data), "bytes (ascii + CRLF)")
