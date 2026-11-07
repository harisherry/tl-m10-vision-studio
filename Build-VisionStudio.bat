@echo off
echo Compiling Thermalright TL-M10 Vision Studio...
set CSC="C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
if not exist %CSC% set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

%CSC% /nologo /target:winexe /platform:x64 /optimize+ ^
  /win32icon:"%~dp0bin\VisionStudio.ico" ^
  /out:"%~dp0bin\VisionStudio.exe" ^
  /r:"%~dp0bin\Microsoft.Web.WebView2.Core.dll" ^
  /r:"%~dp0bin\Microsoft.Web.WebView2.WinForms.dll" ^
  /r:"%~dp0bin\Newtonsoft.Json.dll" ^
  /r:"%~dp0bin\CSCore.dll" ^
  /r:"%~dp0bin\UsbHid.dll" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll ^
  "%~dp0src\Program.cs"

if %ERRORLEVEL% EQU 0 (
    echo Build succeeded: %~dp0bin\VisionStudio.exe
) else (
    echo Build failed with code %ERRORLEVEL%
)
