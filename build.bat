@echo off
rem Command-line build (no IDE needed): produces a 64-bit CdRipper.exe next to this file.
rem Uses the v2.0.50727 compiler (the real csc.exe for .NET 2.0/3.5 projects - the v3.5
rem folder usually has no compiler binary of its own), preferring the 64-bit copy so the
rem /platform:x64 output isn't built against mismatched reference assemblies.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v2.0.50727\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v2.0.50727\csc.exe
if not exist "%CSC%" (
    echo Could not find csc.exe for .NET Framework 2.0/3.5.
    pause
    exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:icon.ico /out:CdRipper.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll *.cs
if errorlevel 1 (echo BUILD FAILED) else (echo Built CdRipper.exe)
pause
