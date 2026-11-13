@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
cd /d "%~dp0"
"%CSC%" /nologo /optimize /out:hpkbd.exe /main:HpKbd.Program /r:System.Windows.Forms.dll hpkbd.cs app.cs || exit /b 1
"%CSC%" /nologo /optimize /out:hpkbd-daemon.exe /main:HpKbd.DaemonEntry /target:winexe /r:System.Windows.Forms.dll hpkbd.cs app.cs || exit /b 1
echo Built hpkbd.exe and hpkbd-daemon.exe
