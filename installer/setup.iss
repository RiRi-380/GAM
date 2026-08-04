[Setup]
AppName=Gmod Addon Manager
AppId=Gmod Addon Manager
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}.0
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
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.ManagedCleanupFailed=Setup could not remove an obsolete application file:%n%n%1%n%nClose GAM and try again.
japanese.ManagedCleanupFailed=古いアプリケーションファイルを削除できませんでした。%n%n%1%n%nGAMを終了して、もう一度お試しください。
english.ManagedManifestInvalid=Setup could not safely validate the previous GAM release-file manifest. Uninstall GAM from Windows Settings, then run this setup again. Your AppData configuration is preserved.
japanese.ManagedManifestInvalid=以前のGAMの管理対象ファイル一覧を安全に検証できませんでした。Windowsの「インストールされているアプリ」からGAMをアンインストールし、このセットアップをもう一度実行してください。AppDataの設定は保持されます。
english.LegacyAdminInstallFound=GAM 1.x is still installed for all users:%n%n%1%n%nUninstall the old GAM from Windows Settings, then run this setup again. The old executable must not be started after GAM 2.0 migration because it can recreate the legacy Workshop layout.%n%nSetup has not removed anything automatically.
japanese.LegacyAdminInstallFound=全ユーザー向けのGAM 1.xが残っています。%n%n%1%n%nWindowsの「インストールされているアプリ」から古いGAMをアンインストールし、このセットアップをもう一度実行してください。2.0への移行後に古い実行ファイルを起動すると、旧Workshopレイアウトが再作成される可能性があります。%n%nセットアップは何も自動削除していません。
english.UnmanagedPreviousInstallFound=An older GAM installation without a managed release manifest was found:%n%n%1%n%nUninstall that GAM from Windows Settings, then run this setup again. Your configuration under AppData is not removed by the uninstaller. Setup has not removed anything automatically.
japanese.UnmanagedPreviousInstallFound=管理対象ファイル一覧を持たない古いGAMが見つかりました。%n%n%1%n%nWindowsの「インストールされているアプリ」からそのGAMをアンインストールし、このセットアップをもう一度実行してください。AppDataの設定はアンインストーラーから削除されません。セットアップは何も自動削除していません。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Extract a temporary copy before installation so the previous managed-file
; manifest can be compared without deleting files that remain in this release.
Source: "..\publish\GAM-ReleaseFiles.txt"; Flags: dontcopy
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\Gmod Addon Manager"; Filename: "{app}\GmodAddonManager.UI.exe"
Name: "{group}\{cm:UninstallProgram,Gmod Addon Manager}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Gmod Addon Manager"; Filename: "{app}\GmodAddonManager.UI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GmodAddonManager.UI.exe"; Description: "{cm:LaunchProgram,Gmod Addon Manager}"; Flags: nowait postinstall shellexec; Check: ShouldLaunchApplication

[Code]
const
  ManagedManifestName = 'GAM-ReleaseFiles.txt';
  LegacyUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Gmod Addon Manager_is1';
  ProductName = 'Gmod Addon Manager';
  ProductPublisher = 'RiRi-380';

function IsGAMDisplayName(Value: String): Boolean;
begin
  { Inno may register a localized suffix, for example the observed Japanese
    value "Gmod Addon Manager バージョン 1.0.0". Accept only the exact
    product name or that name followed by a space-delimited suffix. }
  Result := (CompareText(Value, ProductName) = 0) or
    ((Length(Value) > Length(ProductName)) and
     (CompareText(Copy(Value, 1, Length(ProductName) + 1), ProductName + ' ') = 0));
end;

function TryGetLegacyAdminInstall(var InstallPath: String): Boolean;
var
  DisplayName: String;
  DisplayVersion: String;
  Publisher: String;
  RegisteredInstallPath: String;
  UninstallCommand: String;
begin
  Result := False;
  InstallPath := '';

  { v1 admin installers used the implicit AppId "Gmod Addon Manager" and
    64-bit HKLM uninstall registration. Validate the registered location and
    executable too, including when the user selected a custom destination. }
  if (not RegQueryStringValue(HKLM64, LegacyUninstallKey, 'DisplayName', DisplayName)) or
     (not RegQueryStringValue(HKLM64, LegacyUninstallKey, 'DisplayVersion', DisplayVersion)) or
     (not RegQueryStringValue(HKLM64, LegacyUninstallKey, 'Publisher', Publisher)) or
     (not RegQueryStringValue(HKLM64, LegacyUninstallKey, 'InstallLocation', RegisteredInstallPath)) or
     (not RegQueryStringValue(HKLM64, LegacyUninstallKey, 'UninstallString', UninstallCommand)) then
  begin
    Exit;
  end;

  RegisteredInstallPath := RemoveBackslashUnlessRoot(Trim(RegisteredInstallPath));
  if RegisteredInstallPath = '' then
  begin
    Exit;
  end;
  RegisteredInstallPath := ExpandFileName(RegisteredInstallPath);
  if (not IsGAMDisplayName(DisplayName)) or
     (CompareText(Publisher, ProductPublisher) <> 0) or
     (Copy(DisplayVersion, 1, 2) <> '1.') or
     (Pos(Lowercase(AddBackslash(RegisteredInstallPath)), Lowercase(UninstallCommand)) = 0) or
     (Pos('unins', Lowercase(UninstallCommand)) = 0) or
     (not FileExists(AddBackslash(RegisteredInstallPath) + 'GmodAddonManager.UI.exe')) then
  begin
    Exit;
  end;

  InstallPath := RegisteredInstallPath;
  Result := True;
end;

function TryGetUnmanagedPerUserInstall(var InstallPath: String): Boolean;
var
  DisplayName: String;
  Publisher: String;
  RegisteredInstallPath: String;
  UninstallCommand: String;
begin
  Result := False;
  InstallPath := '';

  { All previous per-user v1/v2 installers used this exact AppId. A present
    executable plus uninstall registration and an absent manifest identifies a
    pre-manifest install, including a custom destination, without scanning or
    deleting the folder. }
  if (not RegQueryStringValue(HKCU, LegacyUninstallKey, 'DisplayName', DisplayName)) or
     (not RegQueryStringValue(HKCU, LegacyUninstallKey, 'Publisher', Publisher)) or
     (not RegQueryStringValue(HKCU, LegacyUninstallKey, 'InstallLocation', RegisteredInstallPath)) or
     (not RegQueryStringValue(HKCU, LegacyUninstallKey, 'UninstallString', UninstallCommand)) then
  begin
    Exit;
  end;

  RegisteredInstallPath := RemoveBackslashUnlessRoot(Trim(RegisteredInstallPath));
  if RegisteredInstallPath = '' then
  begin
    Exit;
  end;
  RegisteredInstallPath := ExpandFileName(RegisteredInstallPath);
  if (not IsGAMDisplayName(DisplayName)) or
     (CompareText(Publisher, ProductPublisher) <> 0) or
     (Pos(Lowercase(AddBackslash(RegisteredInstallPath)), Lowercase(UninstallCommand)) = 0) or
     (Pos('unins', Lowercase(UninstallCommand)) = 0) or
     (not FileExists(AddBackslash(RegisteredInstallPath) + 'GmodAddonManager.UI.exe')) or
     FileExists(AddBackslash(RegisteredInstallPath) + ManagedManifestName) then
  begin
    Exit;
  end;

  InstallPath := RegisteredInstallPath;
  Result := True;
end;

function InitializeSetup(): Boolean;
var
  LegacyInstallPath: String;
  UnmanagedInstallPath: String;
begin
  Result := True;
  if TryGetLegacyAdminInstall(LegacyInstallPath) then
  begin
    MsgBox(
      FmtMessage(ExpandConstant('{cm:LegacyAdminInstallFound}'), [LegacyInstallPath]),
      mbCriticalError,
      MB_OK);
    Result := False;
  end
  else if TryGetUnmanagedPerUserInstall(UnmanagedInstallPath) then
  begin
    MsgBox(
      FmtMessage(ExpandConstant('{cm:UnmanagedPreviousInstallFound}'), [UnmanagedInstallPath]),
      mbCriticalError,
      MB_OK);
    Result := False;
  end;
end;

function ShouldLaunchApplication(): Boolean;
begin
  Result := (not WizardSilent) or
    (CompareText(ExpandConstant('{param:LAUNCHAFTERINSTALL|0}'), '1') = 0);
end;

function NormalizeManagedPath(Value: String): String;
begin
  Result := Trim(Value);
  StringChangeEx(Result, '/', '\', True);
end;

function IsSafeManagedPath(RelativePath: String): Boolean;
var
  AppRoot: String;
  Candidate: String;
begin
  RelativePath := NormalizeManagedPath(RelativePath);
  Result := False;

  if (RelativePath = '') or
     (RelativePath[1] = '\') or
     (Pos(':', RelativePath) > 0) or
     (Pos('*', RelativePath) > 0) or
     (Pos('?', RelativePath) > 0) or
     (CompareText(RelativePath, '..') = 0) or
     (Pos('..\', RelativePath) = 1) or
     (Pos('\..\', RelativePath) > 0) or
     ((Length(RelativePath) >= 3) and
      (CompareText(Copy(RelativePath, Length(RelativePath) - 2, 3), '\..') = 0)) then
  begin
    Exit;
  end;

  AppRoot := AddBackslash(ExpandFileName(ExpandConstant('{app}')));
  Candidate := ExpandFileName(AppRoot + RelativePath);
  Result := CompareText(Copy(Candidate, 1, Length(AppRoot)), AppRoot) = 0;
end;

function ManifestContains(Paths: TArrayOfString; RelativePath: String): Boolean;
var
  Index: Integer;
  NormalizedPath: String;
begin
  Result := False;
  NormalizedPath := NormalizeManagedPath(RelativePath);
  for Index := 0 to GetArrayLength(Paths) - 1 do
  begin
    if CompareText(NormalizeManagedPath(Paths[Index]), NormalizedPath) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function RemoveObsoleteManagedFiles(): String;
var
  OldManifestPath: String;
  NewManifestPath: String;
  OldPaths: TArrayOfString;
  NewPaths: TArrayOfString;
  Index: Integer;
  RelativePath: String;
  ManagedFilePath: String;
begin
  Result := '';
  OldManifestPath := AddBackslash(ExpandConstant('{app}')) + ManagedManifestName;
  if not FileExists(OldManifestPath) then
  begin
    Exit;
  end;

  ExtractTemporaryFile(ManagedManifestName);
  NewManifestPath := AddBackslash(ExpandConstant('{tmp}')) + ManagedManifestName;
  if (not LoadStringsFromFile(OldManifestPath, OldPaths)) or
     (not LoadStringsFromFile(NewManifestPath, NewPaths)) then
  begin
    Result := ExpandConstant('{cm:ManagedManifestInvalid}');
    Exit;
  end;

  for Index := 0 to GetArrayLength(NewPaths) - 1 do
  begin
    if not IsSafeManagedPath(NewPaths[Index]) then
    begin
      Result := ExpandConstant('{cm:ManagedManifestInvalid}');
      Exit;
    end;
  end;

  for Index := 0 to GetArrayLength(OldPaths) - 1 do
  begin
    RelativePath := NormalizeManagedPath(OldPaths[Index]);
    if not IsSafeManagedPath(RelativePath) then
    begin
      Result := ExpandConstant('{cm:ManagedManifestInvalid}');
      Exit;
    end
    else if not ManifestContains(NewPaths, RelativePath) then
    begin
      { A file below a subdirectory could be reached through a junction or
        another reparse point created after the previous install. Inno Setup's
        Pascal Script API does not expose a dependable reparse-point check, so
        obsolete nested paths fail closed and require a manual uninstall. }
      if Pos('\', RelativePath) > 0 then
      begin
        Log('Refusing to remove obsolete nested managed file: ' + RelativePath);
        Result := ExpandConstant('{cm:ManagedManifestInvalid}');
        Exit;
      end;

      ManagedFilePath := AddBackslash(ExpandConstant('{app}')) + RelativePath;
      if FileExists(ManagedFilePath) then
      begin
        Log('Removing obsolete managed file: ' + ManagedFilePath);
        if not DeleteFile(ManagedFilePath) then
        begin
          Result := FmtMessage(ExpandConstant('{cm:ManagedCleanupFailed}'), [ManagedFilePath]);
          Exit;
        end;
      end;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := False;
  Result := RemoveObsoleteManagedFiles();
end;
