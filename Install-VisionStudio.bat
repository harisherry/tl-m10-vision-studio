@echo off
title Install Thermalright TL-M10 Vision Studio
set "INSTALL_DIR=C:\TL-M10-Vision-Studio"

taskkill /F /IM VisionStudio.exe >nul 2>&1
taskkill /F /IM TRCC.exe >nul 2>&1
ping 127.0.0.1 -n 2 >nul

if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
if not exist "%INSTALL_DIR%\bin" mkdir "%INSTALL_DIR%\bin"
if not exist "%INSTALL_DIR%\ui" mkdir "%INSTALL_DIR%\ui"

if exist "%INSTALL_DIR%\bin\vision_studio.log" del /F /Q "%INSTALL_DIR%\bin\vision_studio.log"

reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "TLM10VisionStudio" /t REG_SZ /d "\"C:\TL-M10-Vision-Studio\bin\VisionStudio.exe\" --autostart" /f >nul 2>&1

xcopy /Y /Q "%~dp0bin\*" "%INSTALL_DIR%\bin\" >nul
xcopy /Y /Q "%~dp0ui\*" "%INSTALL_DIR%\ui\" >nul
copy /Y "%~dp0Launch-VisionStudio.bat" "%INSTALL_DIR%\" >nul

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ws = New-Object -ComObject WScript.Shell; " ^
  "foreach ($dt in @(\"$env:USERPROFILE\Desktop\", 'C:\Users\Public\Desktop', [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) { " ^
  "  if ($dt -and (Test-Path $dt)) { " ^
  "    try { " ^
  "      $s = $ws.CreateShortcut((Join-Path $dt 'TL-M10 Vision Studio.lnk')); " ^
  "      $s.TargetPath = 'C:\TL-M10-Vision-Studio\bin\VisionStudio.exe'; " ^
  "      $s.WorkingDirectory = 'C:\TL-M10-Vision-Studio\bin'; " ^
  "      $s.IconLocation = 'C:\TL-M10-Vision-Studio\bin\VisionStudio.ico,0'; " ^
  "      $s.Description = 'Thermalright TL-M10 Vision 1920x462 LCD Studio'; " ^
  "      $s.Save(); " ^
  "    } catch {} " ^
  "  } " ^
  "}"

start "" "C:\TL-M10-Vision-Studio\bin\VisionStudio.exe"
exit /b 0
