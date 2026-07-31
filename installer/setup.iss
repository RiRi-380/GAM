[Setup]
AppName=Gmod Addon Manager
AppVersion={#MyAppVersion}
AppPublisher=RiRi-380
AppPublisherURL=https://github.com/RiRi-380/GAM
DefaultDirName={autopf}\GmodAddonManager
DefaultGroupName=Gmod Addon Manager
UninstallDisplayIcon={app}\GmodAddonManager.UI.exe
Compression=lzma2
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=GAM-Setup-{#MyAppVersion}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\GmodAddonManager.UI\Assets\app.ico
LicenseFile=..\LICENSE

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

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
Filename: "{app}\GmodAddonManager.UI.exe"; Description: "{cm:LaunchProgram,Gmod Addon Manager}"; Flags: nowait postinstall skipifsilent shellexec
Filename: "{app}\GmodAddonManager.UI.exe"; Flags: nowait skipifnotsilent shellexec

[Code]
var
  VCRedistNeedsInstallFlag: Boolean;

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
  
  if VCRedistNeedsInstallFlag then
  begin
    MsgBox('Visual C++ 再頒布可能パッケージがインストールされていません。' + #13#10 +
           'セットアップ中に自動的にインストールされます。', mbInformation, MB_OK);
  end;
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
      if not Exec(ExpandConstant('{tmp}\VC_redist.x64.exe'), '/quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      begin
        MsgBox('Visual C++ 再頒布可能パッケージのインストールに失敗しました。' + #13#10 +
               'エラーコード: ' + IntToStr(ResultCode), mbError, MB_OK);
      end;
    end;
  end;
end;
