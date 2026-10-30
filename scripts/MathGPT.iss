#ifndef Stage
  #error Stage is required
#endif
#ifndef Architecture
  #define Architecture "win-x64"
#endif
#ifndef OutputPath
  #define OutputPath "..\build\packages"
#endif
#ifndef AppVersion
  #error AppVersion must be supplied from Directory.Build.props by Build.ps1
#endif
#ifndef SigningMode
  #define SigningMode "Production"
#endif
#ifndef ExternalSigning
  #define ExternalSigning "None"
#endif
#if SigningMode != "Production" && SigningMode != "Preview"
  #error SigningMode must be Production or Preview
#endif
#if ExternalSigning != "None" && ExternalSigning != "Prepare" && ExternalSigning != "Package"
  #error ExternalSigning must be None, Prepare or Package
#endif
#if SigningMode == "Production"
  #if ExternalSigning == "None"
    #ifndef SigningWorkspace
      #error Production requires a signing workspace and the MathGPTWindows SignTool configured by Build.ps1
    #endif
  #else
    #ifndef SignedUninstallerPath
      #error External signing requires an explicit SignedUninstallerPath
    #endif
  #endif
  #define PackageSuffix ""
#else
  #if ExternalSigning != "None"
    #error External signing cannot be combined with Preview
  #endif
  #define PackageSuffix "-Preview"
#endif
[Setup]
AppId={{8FDC56F2-AF1D-4F1A-B05F-8A2D2FD25BB5}
AppName=MathGPT
AppVersion={#AppVersion}
AppPublisher=MathGPT
AppPublisherURL=https://mathgpt.xyz
AppSupportURL=https://mathgpt.xyz/docs
AppContact=ndls11235@gmail.com
DefaultDirName={localappdata}\Programs\MathGPT
DefaultGroupName=MathGPT
PrivilegesRequired=lowest
#if Architecture == "win-arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19045
OutputDir={#OutputPath}
OutputBaseFilename=MathGPT-{#AppVersion}{#PackageSuffix}-Setup-{#Architecture}
#if SigningMode == "Production"
SignedUninstaller=yes
#if ExternalSigning == "None"
SignTool=MathGPTWindows
SignedUninstallerDir={#SigningWorkspace}\uninstallers
SignToolRetryCount=0
#else
SignedUninstallerDir={#SignedUninstallerPath}
#endif
#else
SignedUninstaller=no
#endif
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\MathGPT.exe
CloseApplications=no
RestartApplications=no
SetupIconFile=..\src\MathGPT.App\Assets\MathGPT.ico
UninstallDisplayName=MathGPT
VersionInfoDescription=MathGPT Desktop Installer
VersionInfoProductName=MathGPT
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
LicenseFile=..\LICENSE

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
#if SigningMode == "Production" && ExternalSigning != "Prepare"
Source: "{#Stage}\MathGPT.exe"; DestDir: "{app}"; Flags: ignoreversion signcheck
Source: "{#Stage}\MathGPT.Bridge.exe"; DestDir: "{app}"; Flags: ignoreversion signcheck
Source: "{#Stage}\*"; DestDir: "{app}"; Excludes: "MathGPT.exe,MathGPT.Bridge.exe"; Flags: ignoreversion recursesubdirs createallsubdirs
#else
Source: "{#Stage}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Icons]
Name: "{userprograms}\MathGPT"; Filename: "{app}\MathGPT.exe"; Check: ShouldCreateShortcuts
Name: "{userdesktop}\MathGPT"; Filename: "{app}\MathGPT.exe"; Check: ShouldCreateShortcuts

[Run]
Filename: "{app}\MathGPT.exe"; Description: "启动 MathGPT"; Flags: nowait postinstall skipifsilent

; No UninstallDelete rules: user data, workspaces and independent clients survive uninstall.

[Code]
function ShouldCreateShortcuts: Boolean;
begin
  Result := ExpandConstant('{param:MATHGPT_NO_SHORTCUTS|0}') <> '1';
end;
