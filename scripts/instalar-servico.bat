@echo off
setlocal
net session >nul 2>&1
if %errorLevel% neq 0 (
  echo A pedir permissoes de administrador...
  powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
set "DIR=%~dp0"
echo A instalar o servico SIBHIK a partir de: %DIR%SIBHIK.exe
sc stop SIBHIK >nul 2>&1
sc delete SIBHIK >nul 2>&1
sc create SIBHIK binPath= "%DIR%SIBHIK.exe" start= auto DisplayName= "SIBHIK - Hikvision para SQL"
sc description SIBHIK "Grava as picagens dos terminais Hikvision na base de dados SQL."
sc failure SIBHIK reset= 60 actions= restart/5000/restart/5000/restart/5000
sc start SIBHIK
echo.
echo ================================================
echo Servico SIBHIK instalado e a arrancar com o Windows.
echo Pode confirmar em services.msc (procurar SIBHIK).
echo ================================================
pause
