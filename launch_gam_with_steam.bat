@echo off
REM GAMをSteam経由で起動するスクリプト
REM Workshopサブスクライブ機能を有効にするために必要

echo ========================================
echo  Gmod Addon Manager - Steam Launcher
echo ========================================
echo.
echo 重要: Workshopアドオンのサブスクライブ機能を使用するには、
echo       GAMをApp ID 4000（Garry's Mod）で起動する必要があります。
echo.

REM 環境変数を設定（Garry's Mod App ID）
set SteamAppId=4000
set SteamGameId=4000

REM 現在のディレクトリを保存
set ORIGINAL_DIR=%CD%

REM インストール済みGAMの場所を確認
if exist "%ProgramFiles%\GmodAddonManager\GmodAddonManager.UI.exe" (
    echo インストール済みのGAMが見つかりました
    set GAM_PATH=%ProgramFiles%\GmodAddonManager\GmodAddonManager.UI.exe
    set GAM_DIR=%ProgramFiles%\GmodAddonManager
    goto :found
)

REM ポータブル版の確認
if exist "%~dp0publish\GmodAddonManager.UI.exe" (
    echo ポータブル版のGAMが見つかりました
    set GAM_PATH=%~dp0publish\GmodAddonManager.UI.exe
    set GAM_DIR=%~dp0publish
    goto :found
)

REM 開発版の確認（publish版）
if exist "%~dp0src\GmodAddonManager.UI\bin\Release\net6.0\win-x64\publish\GmodAddonManager.UI.exe" (
    echo 開発版のGAMが見つかりました（publish版）
    set GAM_PATH=%~dp0src\GmodAddonManager.UI\bin\Release\net6.0\win-x64\publish\GmodAddonManager.UI.exe
    set GAM_DIR=%~dp0src\GmodAddonManager.UI\bin\Release\net6.0\win-x64\publish
    goto :found
)

REM 開発版の確認（通常ビルド版）
if exist "%~dp0src\GmodAddonManager.UI\bin\Release\net6.0\win-x64\GmodAddonManager.UI.exe" (
    echo 開発版のGAMが見つかりました（通常ビルド版）
    set GAM_PATH=%~dp0src\GmodAddonManager.UI\bin\Release\net6.0\win-x64\GmodAddonManager.UI.exe
    set GAM_DIR=%~dp0src\GmodAddonManager.UI\bin\Release\net6.0\win-x64
    goto :found
)

echo エラー: GmodAddonManager.UI.exe が見つかりません
echo.
echo GAMをインストールするか、このスクリプトをGAMのフォルダから実行してください
pause
exit /b 1

:found
echo.
echo GAMのパス: %GAM_PATH%
echo GAMのディレクトリ: %GAM_DIR%
echo.

REM steam_appid.txtを作成（GAMの実行ディレクトリに）
echo 4000 > "%GAM_DIR%\steam_appid.txt"
echo steam_appid.txt を作成しました

REM DLLの存在確認
if not exist "%GAM_DIR%\steam_api64.dll" (
    echo 警告: steam_api64.dll が見つかりません
    echo Workshop機能が正しく動作しない可能性があります
    echo.
)

echo.
REM Steamが起動しているか確認
tasklist /FI "IMAGENAME eq steam.exe" 2>NUL | find /I /N "steam.exe">NUL
if "%ERRORLEVEL%"=="1" (
    echo [エラー] Steamが起動していません！
    echo Steamを起動してから、このランチャーを再度実行してください。
    echo.
    pause
    exit /b 1
)

echo [OK] Steamが起動しています
echo.

echo 起動オプション:
echo.
echo [1] 直接起動（推奨）
echo     App ID 4000でGAMを直接起動します
echo.
echo [2] Steamプロトコル経由で起動
echo     SteamのURLスキームを使用して起動します
echo.
echo [ヒント] Garry's ModがSteamにインストールされている必要があります
echo.
set /p CHOICE="選択してください (1=直接起動[推奨], 2=Steam経由): "

if "%CHOICE%"=="2" (
    echo.
    echo Steamプロトコルで起動します...
    echo 注意: この方法が動作しない場合は、直接起動をお試しください。
    start steam://run/4000//"%GAM_PATH%"
) else (
    echo.
    echo GAMをApp ID 4000で直接起動します...
    cd /d "%GAM_DIR%"
    start "" "%GAM_PATH%"
)

echo.
echo GAMを起動しました
echo.
echo ✓ Workshopアドオンのサブスクライブ機能が使用可能になります
echo ✓ Workshop画像が正しく表示されます
echo.
echo GAMが起動しない場合:
echo 1. Garry's ModがSteamにインストールされているか確認
echo 2. 非SteamゲームとしてGAMを追加し、起動オプションに +app_id 4000 を追加
echo.
echo 5秒後にこのウィンドウを閉じます...
timeout /t 5 >nul