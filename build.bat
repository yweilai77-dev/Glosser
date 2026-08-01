@echo off
chcp 65001 >nul
setlocal

set "CSC="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not defined CSC (
    echo [错误] 找不到 .NET Framework 编译器 ^(csc.exe^)
    echo Windows 10/11 通常自带，请检查系统组件。
    pause
    exit /b 1
)

echo 正在编译...
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /win32icon:glosser.ico /out:划词查询.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll 划词查询.cs

if %errorlevel%==0 (
    echo.
    echo 编译成功：划词查询.exe
) else (
    echo.
    echo 编译失败，请检查上方错误信息。
)
pause
