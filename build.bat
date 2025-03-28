@echo off
echo Building QR Code Scanner...
dotnet build QRCodeScanner/QRCodeScanner.csproj -c Release
if %ERRORLEVEL% EQU 0 (
    echo Build successful! Running application...
    start QRCodeScanner\bin\Release\net6.0-windows\QRCodeScanner.exe
) else (
    echo Build failed!
    pause
) 