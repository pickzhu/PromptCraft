[Setup]
AppName=PromptCraft
; 接收来自 GitHub Actions 的动态版本号
AppVersion={#AppVersion}
AppPublisher=PromptCraftTeam
DefaultDirName={autopf}\PromptCraft
DefaultGroupName=PromptCraft
OutputDir=.\output
; 安装包文件名也会带上版本号，例如 PromptCraft-Windows-Setup-1.0.5.exe
OutputBaseFilename=PromptCraft-Windows-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

; 配置安装包本身的图标（你的 logo 路径）
SetupIconFile=.\PromptCraft\Assets\logo.ico

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 添加 Excludes: "*.pdb" 排除所有调试符号文件，确保包体积最小
Source: ".\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PromptCraft"; Filename: "{app}\PromptCraft.Desktop.exe"; IconFilename: "{app}\PromptCraft.Desktop.exe"
Name: "{autodesktop}\PromptCraft"; Filename: "{app}\PromptCraft.Desktop.exe"; Tasks: desktopicon; IconFilename: "{app}\PromptCraft.Desktop.exe"

[Run]
Description: "{cm:LaunchProgram,PromptCraft}"; Filename: "{app}\PromptCraft.Desktop.exe"; Flags: nowait postinstall skipifsilent