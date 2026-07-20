@echo off
setlocal
net session >nul 2>&1
if %errorLevel% neq 0 (
  powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
sc stop SIBHIK
sc delete SIBHIK
echo.
echo Servico SIBHIK removido.
pause
