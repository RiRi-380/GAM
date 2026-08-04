[Setup]
AppName=Gmod Addon Manager
AppId=Gmod Addon Manager
AppVersion={#MyAppVersion}
AppPublisher=RiRi-380
AppPublisherURL=https://github.com/RiRi-380/GAM
DefaultDirName={autopf}\GmodAddonManager
DefaultGroupName=Gmod Addon Manager
UninstallDisplayIcon={app}\GmodAddonManager.UI.exe
Uninstallable=yes
CreateUninstallRegKey=yes
Compression=lzma2
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=GAM-Setup-{#MyAppVersion}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\GmodAddonManager.UI\Assets\app.ico
LicenseFile=..\publish\DISTRIBUTION-LICENSES.txt

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.VCRedistMissing=Microsoft Visual C++ Redistributable is not installed.%n%nSetup will install it automatically.
japanese.VCRedistMissing=Microsoft Visual C++ 再頒布可能パッケージがインストールされていません。%n%nセットアップ中に自動でインストールします。
english.VCRedistLaunchFailed=Microsoft Visual C++ Redistributable could not be started.
japanese.VCRedistLaunchFailed=Microsoft Visual C++ 再頒布可能パッケージを起動できませんでした。
english.VCRedistInstallFailed=Microsoft Visual C++ Redistributable failed with exit code %1.
japanese.VCRedistInstallFailed=Microsoft Visual C++ 再頒布可能パッケージのインストールに失敗しました。終了コード: %1

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; Visual C++ Redistributable - ダウンロードURL: https://aka.ms/vs/17/release/vc_redist.x64.exe
Source: "..\redist\VC_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: VCRedistNeedsInstall

[Icons]
Name: "{group}\Gmod Addon Manager"; Filename: "{app}\GmodAddonManager.UI.exe"
Name: "{group}\{cm:UninstallProgram,Gmod Addon Manager}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Gmod Addon Manager"; Filename: "{app}\GmodAddonManager.UI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GmodAddonManager.UI.exe"; Description: "{cm:LaunchProgram,Gmod Addon Manager}"; Flags: nowait postinstall shellexec; Check: ShouldLaunchApplication

[Code]
var
  VCRedistNeedsInstallFlag: Boolean;
  VCRedistRestartRequiredFlag: Boolean;

// Visual C++ Redistributableがインストールされているかチェック
function IsVCRedistInstalled: Boolean;
var
  Version: Cardinal;
begin
  Result := False;
  
  // Visual C++ 2015-2022 Redistributable (x64)のレジストリキーをチェック
  // 複数のバージョンチェック方法を試す
  
  // 方法1: VC Runtime version check
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Version) then
  begin
    if Version = 1 then
      Result := True;
  end;
  
  // 方法2: VC Runtime major version check (14 = VS2015-2022)
  if not Result then
  begin
    if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Major', Version) then
    begin
      if Version >= 14 then
        Result := True;
    end;
  end;
  
  // 方法3: Uninstaller registry check
  if not Result then
  begin
    // VS2022 version
    Result := RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{A1C31BA5-5438-4A07-9C1E-EAEB19B5D8FC}');
  end;
  
  // 方法4: WOW6432Node check
  if not Result then
  begin
    Result := RegKeyExists(HKLM64, 'SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64');
  end;
end;

// VCRedistNeedsInstallチェック関数
function VCRedistNeedsInstall: Boolean;
begin
  Result := VCRedistNeedsInstallFlag;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  VCRedistNeedsInstallFlag := not IsVCRedistInstalled();
  VCRedistRestartRequiredFlag := False;
  
  if VCRedistNeedsInstallFlag then
  begin
    MsgBox(ExpandConstant('{cm:VCRedistMissing}'), mbInformation, MB_OK);
  end;
end;

function NeedRestart(): Boolean;
begin
  Result := VCRedistRestartRequiredFlag;
end;

function ShouldLaunchApplication(): Boolean;
begin
  Result := (not WizardSilent) or
    (CompareText(ExpandConstant('{param:LAUNCHAFTERINSTALL|0}'), '1') = 0);
end;

// セットアップ完了前にVC++ Redistributableをインストール
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and VCRedistNeedsInstallFlag then
  begin
    // VC++ Redistributableをサイレントインストール
    if FileExists(ExpandConstant('{tmp}\VC_redist.x64.exe')) then
    begin
      if not Exec(ExpandConstant('{tmp}\VC_redist.x64.exe'), '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      begin
        RaiseException(ExpandConstant('{cm:VCRedistLaunchFailed}'));
      end;

      if ResultCode = 3010 then
      begin
        VCRedistRestartRequiredFlag := True;
      end
      else if (ResultCode <> 0) and (ResultCode <> 1638) then
      begin
        RaiseException(FmtMessage(
          ExpandConstant('{cm:VCRedistInstallFailed}'), [IntToStr(ResultCode)]));
      end;
    end
    else
    begin
      RaiseException(ExpandConstant('{cm:VCRedistLaunchFailed}'));
    end;
  end;
end;
