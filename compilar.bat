@echo off
rem Compila o EspacoDisco.exe com o compilador C# que ja vem no Windows (.NET Framework 4.8).
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /out:EspacoDisco.exe /win32manifest:app.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll src\*.cs
if errorlevel 1 (
  echo.
  echo Falhou a compilacao.
  exit /b 1
)
echo Pronto: %~dp0EspacoDisco.exe
