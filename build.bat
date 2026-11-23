@echo off
rem Build RovnyFPS.exe (the merged program: FPS limit + one-click settings) with the .NET Framework compiler.
rem rovny\ = the former "Rovny FPS" (namespace RovnyFps), master\ = the former "FPS-master" (namespace FpsMaster).
rem Entry point is FpsMaster.Program (Programma.cs); Okno.cs is the shared window. Icon: Tabler "gauge" (MIT).
cd /d "%~dp0"
set OUT=%1
if "%OUT%"=="" set OUT=RovnyFPS.exe
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /define:VSTRAIVANIE ^
  /main:FpsMaster.Program /win32icon:znachok\app.ico /resource:znachok\logo64.png,logo64.png ^
  /out:%OUT% /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  Okno.cs Deystviya.cs Programma.cs Vstraivanie.cs ProbaVynos.cs Svedeniya.cs ^
  master\FpsMaster.cs master\Proverki.cs master\Zamer.cs master\Katka.cs master\Nvidia.cs master\Volna4.cs master\Volna5.cs ^
  rovny\RovnyFPS.cs rovny\Obnovlenie.cs rovny\Zamer.cs rovny\Katka.cs
