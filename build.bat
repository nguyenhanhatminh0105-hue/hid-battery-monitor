@echo off
rem Builds BatteryRead.exe with the C# compiler bundled in Windows.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" -nologo -platform:x64 -out:BatteryRead.exe src\BatteryRead.cs
