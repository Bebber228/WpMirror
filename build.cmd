@echo off
cd /d "%~dp0"
set CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /unsafe /win32icon:app.ico /out:WpMirror.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /resource:icons\back.png,back.png /resource:icons\windows.png,windows.png /resource:icons\search.png,search.png /resource:app.ico,app.ico Program.cs Usb.cs
if errorlevel 1 (echo BUILD FAILED) else (echo OK: WpMirror.exe)
pause
