@echo off
setlocal
cd /d "%~dp0"
set "RUSTDESK_HDR_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%RUSTDESK_HDR_CSC%" set "RUSTDESK_HDR_CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%RUSTDESK_HDR_CSC%" (
  echo C# compiler not found. Install the Microsoft .NET Framework Developer Pack.
  if /i not "%~1"=="--ci" pause
  exit /b 1
)
"%RUSTDESK_HDR_CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32manifest:app.manifest /win32icon:app.ico /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /out:RustDeskHDRhelper.exe Program.cs HdrControl.cs
if errorlevel 1 (
  echo Build failed. Please copy the error message.
  if /i not "%~1"=="--ci" pause
  exit /b 1
)
echo Built: %CD%\RustDeskHDRhelper.exe
echo Standalone EXE ready. Stop the old version, then launch this EXE.
echo No HDRCmd.exe is needed.
if /i not "%~1"=="--ci" pause
exit /b 0
