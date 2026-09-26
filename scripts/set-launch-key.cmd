@echo off
rem Runs set-launch-key.ps1 from cmd or Explorer (pwsh if installed, else Windows PowerShell), past the execution policy.
where pwsh >nul 2>nul && (set "PS=pwsh") || (set "PS=powershell")
%PS% -NoProfile -ExecutionPolicy Bypass -File "%~dp0set-launch-key.ps1" %*
set "CODE=%ERRORLEVEL%"
rem Keep the window open when started by double-click, so the public key can be copied.
echo %cmdcmdline% | find /i "%~0" >nul && pause
exit /b %CODE%
