@echo off
rem Build RovnyFPS.exe with the .NET Framework compiler (ships with Windows 10/11)
rem Icon: Tabler "gauge" (MIT), see znachok\gauge.svg
rem Vstraivanie.cs + /define:VSTRAIVANIE = optional embedding into a shell tab (--vstroeno).
rem Without /define:VSTRAIVANIE the same file compiles to no-op stubs.
cd /d "%~dp0"
set OUT=%1
if "%OUT%"=="" set OUT=RovnyFPS.exe
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /define:VSTRAIVANIE ^
  /win32icon:znachok\app.ico /resource:znachok\logo64.png,logo64.png ^
  /out:%OUT% /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll RovnyFPS.cs Vstraivanie.cs Obnovlenie.cs
