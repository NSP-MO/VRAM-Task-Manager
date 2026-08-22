; Inno Setup 6 Script with Automated .NET 8.0 Desktop Runtime Detection & Installation

#define MyAppName "VRAM Task Manager"
#define MyAppVersion "0.1.1"
#define MyAppPublisher "NSP-MO"
#define MyAppURL "https://github.com/NSP-MO/vram-manager"
#define MyAppExeName "VramTaskManager.exe"

[Setup]
AppId={{E8B29C54-610E-4F42-8D3A-05C3FE92B19F}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes

OutputDir=output
OutputBaseFilename=VramTaskManager_Setup_v0.1.1_x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// Function to check if .NET 8.0 Desktop Runtime is installed on the system
function IsDotNet8DesktopRuntimeInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  FindRec: TFindRec;
  SharedPath: string;
begin
  Result := False;

  // 1. Check 64-bit Registry Key
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if (Pos('8.0.', Names[I]) = 1) or (Names[I] = '8.0') then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;

  // 2. Check 32-bit/WOW64 Registry Key
  if RegGetValueNames(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if (Pos('8.0.', Names[I]) = 1) or (Names[I] = '8.0') then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;

  // 3. File System Directory Verification (Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.*)
  SharedPath := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if DirExists(SharedPath) then
  begin
    if FindFirst(SharedPath + '\8.0.*', FindRec) then
    begin
      try
        Result := True;
        Exit;
      finally
        FindClose(FindRec);
      end;
    end;
  end;
end;

// Function to download a file using curl or PowerShell
function DownloadRuntimeInstaller(const DownloadUrl, DestinationPath: string): Boolean;
var
  CurlPath: string;
  PowerShellPath: string;
  PowerShellCmd: string;
  ResultCode: Integer;
begin
  Result := False;

  // Attempt 1: Built-in curl.exe (available natively on Windows 10/11)
  CurlPath := ExpandConstant('{sys}\curl.exe');
  if FileExists(CurlPath) then
  begin
    if Exec(CurlPath, Format('-L -f --retry 3 --connect-timeout 15 -o "%s" "%s"', [DestinationPath, DownloadUrl]), '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      if (ResultCode = 0) and FileExists(DestinationPath) then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;

  // Attempt 2: Fallback to PowerShell WebClient
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  PowerShellCmd := Format('-NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; (New-Object Net.WebClient).DownloadFile(''%s'', ''%s'')"', [DownloadUrl, DestinationPath]);
  
  if Exec(PowerShellPath, PowerShellCmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if (ResultCode = 0) and FileExists(DestinationPath) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

// Function to download and install .NET 8.0 Desktop Runtime automatically
function DownloadAndInstallDotNet8(): Boolean;
var
  DownloadUrl: string;
  InstallerPath: string;
  ResultCode: Integer;
begin
  DownloadUrl := 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  InstallerPath := ExpandConstant('{tmp}\windowsdesktop-runtime-8.0-win-x64.exe');

  // Step 1: Download Microsoft runtime installer
  if not DownloadRuntimeInstaller(DownloadUrl, InstallerPath) then
  begin
    Result := False;
    Exit;
  end;

  // Step 2: Execute silent installation of .NET Runtime
  if not Exec(InstallerPath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := False;
    Exit;
  end;

  // Step 3: Check exit code (0 = success, 3010 = success reboot required) or verify installation
  Result := (ResultCode = 0) or (ResultCode = 3010) or IsDotNet8DesktopRuntimeInstalled();
end;

function InitializeSetup(): Boolean;
begin
  Result := True;

  // Verify .NET 8 Desktop Runtime availability
  if not IsDotNet8DesktopRuntimeInstalled() then
  begin
    if MsgBox(
      'VRAM Task Manager requires the Microsoft .NET 8.0 Desktop Runtime, which was not detected on this system.' + #13#10 + #13#10 +
      'Would you like the installer to automatically download and install it now?',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      if not DownloadAndInstallDotNet8() then
      begin
        MsgBox(
          'Failed to automatically install the .NET 8.0 Desktop Runtime.' + #13#10 + #13#10 +
          'Please download and install it manually from https://dotnet.microsoft.com/download/dotnet/8.0 before running setup.',
          mbError, MB_OK);
        Result := False;
      end;
    end
    else
    begin
      Result := False;
    end;
  end;
end;
