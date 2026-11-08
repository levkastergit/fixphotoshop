@echo off
rem Builds FixPhotoshop.exe with the C# compiler that ships with Windows. Nothing to install.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found: %CSC%
  exit /b 1
)
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /out:FixPhotoshop.exe /win32manifest:src\app.manifest /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll src\*.cs
exit /b %ERRORLEVEL%
