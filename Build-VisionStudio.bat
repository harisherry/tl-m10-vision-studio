@echo off
echo Compiling Thermalright TL-M10 Vision Studio (x64)...
set CSC="C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
if not exist %CSC% set CSC="C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
if not exist %CSC% set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist "%~dp0bin" mkdir "%~dp0bin"
if exist "%~dp0lib\*" xcopy /Y /Q "%~dp0lib\*" "%~dp0bin\" >nul

%CSC% /nologo /target:winexe /platform:x64 /optimize+ ^
  /win32icon:"%~dp0bin\VisionStudio.ico" ^
  /out:"%~dp0bin\VisionStudio.exe" ^
  /r:"%~dp0bin\Microsoft.Web.WebView2.Core.dll" ^
  /r:"%~dp0bin\Microsoft.Web.WebView2.WinForms.dll" ^
  /r:"%~dp0bin\Newtonsoft.Json.dll" ^
  /r:"%~dp0bin\CSCore.dll" ^
  /r:"%~dp0bin\UsbHid.dll" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll ^
  "%~dp0src\Program.cs" ^
  "%~dp0src\TelemetryData.cs" ^
  "%~dp0src\MainForm.cs" ^
  "%~dp0src\LiveMarketDataService.cs" ^
  "%~dp0src\PopoutPreviewForm.cs" ^
  "%~dp0src\ThermalrightUsbTransport.cs" ^
  "%~dp0src\HardwareTelemetryReader.cs" ^
  "%~dp0src\WasapiSpectrumCapture.cs"

if %ERRORLEVEL% EQU 0 (
    echo Build succeeded: %~dp0bin\VisionStudio.exe
) else (
    echo Build failed with code %ERRORLEVEL%
    exit /b %ERRORLEVEL%
)
