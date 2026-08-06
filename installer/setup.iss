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
UninstallLogMode=append
Compression=lzma2
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=GAM-Setup-{#MyAppVersion}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
UsePreviousPrivileges=yes
UsePreviousAppDir=yes
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
english.DuplicateInstallModes=GAM is registered both for the current user and for all users:%n%nCurrent user: %1%nAll users: %2%n%nSetup cannot safely choose which installation to upgrade. Keep the installation you use, remove the duplicate application entry, and run Setup again. Your AppData, GMod settings, and Workshop files are not removed by Setup.
japanese.DuplicateInstallModes=GAMがユーザー単位と全ユーザー向けの両方に登録されています。%n%nユーザー単位: %1%n全ユーザー: %2%n%nどちらを更新するか安全に判断できません。使用する方を残して重複したアプリ登録を削除し、もう一度Setupを実行してください。SetupはAppData、GMod設定、Workshopファイルを削除しません。

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
Name: "{autodesktop}\Gmod Addon Manager"; Filename: "{app}\GmodAddonManager.UI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GmodAddonManager.UI.exe"; Description: "{cm:LaunchProgram,Gmod Addon Manager}"; Flags: nowait postinstall shellexec; Check: ShouldLaunchApplication

[Code]
const
  ManagedManifestName = 'GAM-ReleaseFiles.txt';
  LegacyUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Gmod Addon Manager_is1';
  LegacySteamApi64Name = 'steam_api64.dll';
  LegacySteamApi64Size = 296408;
  LegacySteamApi64Sha256 = '46688ecd8849a86bf8b807c5de1adbb8b8dddaa48583d68b3518b72c77c15bd0';
  LegacySteamAppIdName = 'steam_appid.txt';
  LegacySteamAppIdSize = 4;
  LegacySteamAppIdSha256 = 'b090147020e033534635010c4f7eb6fc270d44e5df67ea9e744a8087df9ca106';

var
  LegacyUserInstallPath: String;
  LegacyAdminInstallPath: String;

function IsGAMDisplayName(Value: String): Boolean;
begin
  Result := (CompareText(Value, 'Gmod Addon Manager') = 0) or
    ((Length(Value) > Length('Gmod Addon Manager')) and
     (CompareText(
        Copy(Value, 1, Length('Gmod Addon Manager') + 1),
        'Gmod Addon Manager ') = 0));
end;

function TryGetRegisteredVersionOneInstall(RootKey: Integer;
  var RegisteredInstallPath: String): Boolean;
var
  DisplayName: String;
  DisplayVersion: String;
  Publisher: String;
  UninstallCommand: String;
  ExecutablePath: String;
  ExecutableMajor: Word;
  ExecutableMinor: Word;
  ExecutableRevision: Word;
  ExecutableBuild: Word;
begin
  Result := False;
  RegisteredInstallPath := '';
  if (not RegQueryStringValue(RootKey, LegacyUninstallKey, 'DisplayName', DisplayName)) or
     (not RegQueryStringValue(RootKey, LegacyUninstallKey, 'DisplayVersion', DisplayVersion)) or
     (not RegQueryStringValue(RootKey, LegacyUninstallKey, 'Publisher', Publisher)) or
     (not RegQueryStringValue(RootKey, LegacyUninstallKey, 'InstallLocation', RegisteredInstallPath)) or
     (not RegQueryStringValue(RootKey, LegacyUninstallKey, 'UninstallString', UninstallCommand)) then
  begin
    Exit;
  end;

  DisplayVersion := Trim(DisplayVersion);
  RegisteredInstallPath := RemoveBackslashUnlessRoot(Trim(RegisteredInstallPath));
  if RegisteredInstallPath = '' then
  begin
    Exit;
  end;
  RegisteredInstallPath := ExpandFileName(RegisteredInstallPath);
  ExecutablePath := AddBackslash(RegisteredInstallPath) + 'GmodAddonManager.UI.exe';

  Result := IsGAMDisplayName(DisplayName) and
    (CompareText(Publisher, 'RiRi-380') = 0) and
    (Length(DisplayVersion) > 2) and
    (CompareText(Copy(DisplayVersion, 1, 2), '1.') = 0) and
    (Pos(Lowercase(AddBackslash(RegisteredInstallPath)), Lowercase(UninstallCommand)) > 0) and
    (Pos('unins', Lowercase(UninstallCommand)) > 0) and
    FileExists(ExecutablePath) and
    GetVersionComponents(
      ExecutablePath,
      ExecutableMajor,
      ExecutableMinor,
      ExecutableRevision,
      ExecutableBuild) and
    (ExecutableMajor = 1);
  if not Result then
  begin
    RegisteredInstallPath := '';
  end;
end;

function InitializeSetup(): Boolean;
begin
  { v1.0.6-v1.0.26 launch Setup silently without the v2-only
    /LAUNCHAFTERINSTALL flag. Remember the registered v1 before Setup updates
    the uninstall record so a successful legacy update still reopens GAM. }
  TryGetRegisteredVersionOneInstall(HKCU, LegacyUserInstallPath);
  TryGetRegisteredVersionOneInstall(HKLM64, LegacyAdminInstallPath);
  if (LegacyUserInstallPath <> '') and (LegacyAdminInstallPath <> '') then
  begin
    MsgBox(
      FmtMessage(ExpandConstant('{cm:DuplicateInstallModes}'), [LegacyUserInstallPath, LegacyAdminInstallPath]),
      mbCriticalError,
      MB_OK);
    Result := False;
    Exit;
  end
  else if (LegacyUserInstallPath <> '') or (LegacyAdminInstallPath <> '') then
  begin
    Log('Registered GAM v1 installation detected; legacy upgrade compatibility is available.');
  end;
  Result := True;
end;

function IsSelectedLegacyV1Upgrade(): Boolean;
var
  SelectedPath: String;
begin
  SelectedPath := ExpandFileName(ExpandConstant('{app}'));
  if IsAdminInstallMode then
  begin
    Result := (LegacyAdminInstallPath <> '') and
      (CompareText(SelectedPath, LegacyAdminInstallPath) = 0);
  end
  else
  begin
    Result := (LegacyUserInstallPath <> '') and
      (CompareText(SelectedPath, LegacyUserInstallPath) = 0);
  end;
end;

function ShouldLaunchApplication(): Boolean;
begin
  Result := (not WizardSilent) or
    (CompareText(ExpandConstant('{param:LAUNCHAFTERINSTALL|0}'), '1') = 0) or
    IsSelectedLegacyV1Upgrade();
end;

procedure RemoveKnownLegacyFile(FileName: String; ExpectedSize: Int64;
  ExpectedSha256: String);
var
  FilePath: String;
  ActualSize: Int64;
  ActualSha256: String;
begin
  FilePath := AddBackslash(ExpandConstant('{app}')) + FileName;
  if not FileExists(FilePath) then
  begin
    Exit;
  end;

  if (not FileSize64(FilePath, ActualSize)) or (ActualSize <> ExpectedSize) then
  begin
    Log('Preserving non-matching legacy filename: ' + FilePath);
    Exit;
  end;

  try
    ActualSha256 := GetSHA256OfFile(FilePath);
  except
    Log('Could not hash potential legacy file; preserving it: ' + FilePath);
    Exit;
  end;

  if CompareText(ActualSha256, ExpectedSha256) <> 0 then
  begin
    Log('Preserving legacy filename with an unknown hash: ' + FilePath);
    Exit;
  end;

  if DeleteFile(FilePath) then
  begin
    Log('Removed verified obsolete GAM v1 file: ' + FilePath);
  end
  else
  begin
    { These files are not used by v2. Failure to remove one must not turn a
      successfully copied v2 application into a failed upgrade. }
    Log('Could not remove verified obsolete GAM v1 file: ' + FilePath);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and IsSelectedLegacyV1Upgrade() then
  begin
    { Never delete by filename alone: source builds may contain user-supplied
      files with these names. Only the two exact historical v1 payloads are
      eligible, and cleanup happens after v2 files have copied successfully. }
    RemoveKnownLegacyFile(
      LegacySteamApi64Name,
      LegacySteamApi64Size,
      LegacySteamApi64Sha256);
    RemoveKnownLegacyFile(
      LegacySteamAppIdName,
      LegacySteamAppIdSize,
      LegacySteamAppIdSha256);
  end;
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
