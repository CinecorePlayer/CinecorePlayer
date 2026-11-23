; Cinecore Player - installer Windows (Inno Setup 6.6+).
; Si compila con Build-Installer.ps1, che pubblica l'app e passa /DPayloadDir e /DOutputDir.

#ifndef PayloadDir
  #define PayloadDir "..\artifacts\installer\payload"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

; Versione e nome del file arrivano da Build-Installer.ps1, che li legge dal progetto
; (<Version> e <InformationalVersion> in CinecorePlayer2025.csproj).
#define AppName "Cinecore Player 2026"
#ifndef AppVersion
  #define AppVersion "Beta 4.1"
#endif
#ifndef AppVersionNumber
  #define AppVersionNumber "0.4.1"
#endif
#ifndef OutputName
  #define OutputName "CinecorePlayer2026-Beta4.1-Windows-Setup"
#endif
#ifndef AppExe
  #define AppExe "CinecorePlayer2026.exe"
#endif
#ifndef LicenseDir
  #define LicenseDir ".."
#endif
#define AppPublisher "Cinecore"

[Setup]
; Identita' fissa: gli aggiornamenti successivi sostituiscono questa installazione.
AppId={{6F3C2B1E-8E4A-4C57-9B0D-3C1E2A7F5D21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersionNumber}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
LicenseFile={#LicenseDir}\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
SetupIconFile=..\win-native\CinecoreWin\cinecore.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=4
; Aspetto: stile Windows 11, chiaro o scuro come il sistema, immagini Cinecore per ogni scala DPI.
WizardStyle=modern dynamic windows11
WizardImageFile=assets\wizard-100.png,assets\wizard-125.png,assets\wizard-150.png,assets\wizard-175.png,assets\wizard-200.png,assets\wizard-250.png
WizardSmallImageFile=assets\small-100.png,assets\small-125.png,assets\small-150.png,assets\small-175.png,assets\small-200.png,assets\small-250.png
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage

[Languages]
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
it.GroupComponents=Componenti di riproduzione (consigliati):
en.GroupComponents=Playback components (recommended):
it.TaskLav=LAV Filters 0.80 - decoder audio e video DirectShow
en.TaskLav=LAV Filters 0.80 - DirectShow audio and video decoders
it.TaskMpcAudioDecoder=MPC Audio Decoder x64
en.TaskMpcAudioDecoder=MPC Audio Decoder x64
it.TaskMpcAudioRenderer=MPC Audio Renderer x64
en.TaskMpcAudioRenderer=MPC Audio Renderer x64
it.TaskMadVr=madVR 0.92.17 - renderer video (registrazione del filtro)
en.TaskMadVr=madVR 0.92.17 - video renderer (filter registration)
it.TaskMpcVr=MPC Video Renderer - renderer video (registrazione del filtro)
en.TaskMpcVr=MPC Video Renderer - video renderer (filter registration)
it.TaskXySub=XySubFilter - sottotitoli (registrazione del filtro)
en.TaskXySub=XySubFilter - subtitles (filter registration)
it.TaskWebView2=Microsoft Edge WebView2 Runtime (download da Microsoft, necessario per YouTube e pagine web)
en.TaskWebView2=Microsoft Edge WebView2 Runtime (downloaded from Microsoft, required for YouTube and web pages)
it.StatusRegistering=Registrazione dei filtri video...
en.StatusRegistering=Registering video filters...
it.StatusComponent=Installazione di %1...
en.StatusComponent=Installing %1...
it.StatusWebView2=Download di WebView2 Runtime da Microsoft...
en.StatusWebView2=Downloading WebView2 Runtime from Microsoft...
it.WebView2Failed=Non è stato possibile scaricare WebView2 Runtime (%1). Potrai installarlo più tardi da microsoft.com.
en.WebView2Failed=WebView2 Runtime could not be downloaded (%1). You can install it later from microsoft.com.
it.FinishedInfo=Cinecore Player è pronto. I componenti di terze parti hanno le proprie licenze: le trovi in THIRD-PARTY-CREDITS.md nella cartella del programma.
en.FinishedInfo=Cinecore Player is ready. Third-party components have their own licenses: see THIRD-PARTY-CREDITS.md in the program folder.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "lav"; Description: "{cm:TaskLav}"; GroupDescription: "{cm:GroupComponents}"
Name: "mpcadec"; Description: "{cm:TaskMpcAudioDecoder}"; GroupDescription: "{cm:GroupComponents}"
Name: "mpcaren"; Description: "{cm:TaskMpcAudioRenderer}"; GroupDescription: "{cm:GroupComponents}"
Name: "madvr"; Description: "{cm:TaskMadVr}"; GroupDescription: "{cm:GroupComponents}"
Name: "mpcvr"; Description: "{cm:TaskMpcVr}"; GroupDescription: "{cm:GroupComponents}"
Name: "xysub"; Description: "{cm:TaskXySub}"; GroupDescription: "{cm:GroupComponents}"
Name: "webview2"; Description: "{cm:TaskWebView2}"; GroupDescription: "{cm:GroupComponents}"; Check: not IsWebView2Installed

[InstallDelete]
; Resti del vecchio setup autoestraente (Beta 1).
Type: files; Name: "{app}\Install-Cinecore.ps1"
Type: files; Name: "{app}\Installer-Components.log"
; Fino alla Beta 3.6 l'eseguibile si chiamava CinecorePlayer2025: via il vecchio programma e i suoi collegamenti.
Type: files; Name: "{app}\CinecorePlayer2025.exe"
Type: files; Name: "{app}\CinecorePlayer2025.dll"
Type: files; Name: "{app}\CinecorePlayer2025.deps.json"
Type: files; Name: "{app}\CinecorePlayer2025.runtimeconfig.json"
Type: files; Name: "{app}\CinecorePlayer2025.pdb"
Type: files; Name: "{autoprograms}\Cinecore Player.lnk"
Type: files; Name: "{autodesktop}\Cinecore Player.lnk"
#ifdef AvaloniaBuild
; Beta 4 per Windows (nuova interfaccia): via l'eseguibile del player precedente (CinecorePlayer2026.exe, WinForms)
; e i suoi file; dati, impostazioni e libreria restano in %APPDATA%\CinecorePlayer2025.
Type: files; Name: "{app}\CinecorePlayer2026.exe"
Type: files; Name: "{app}\CinecorePlayer2026.dll"
Type: files; Name: "{app}\CinecorePlayer2026.deps.json"
Type: files; Name: "{app}\CinecorePlayer2026.runtimeconfig.json"
Type: files; Name: "{app}\CinecorePlayer2026.pdb"
Type: filesandordirs; Name: "{app}\third-parties\stem-runtime"
; La Beta 4 del 20/11/2026 poteva avere l'eseguibile CinecorePlayer.exe: via, ora si chiama {#AppExe}.
Type: files; Name: "{app}\CinecorePlayer.exe"
Type: files; Name: "{app}\CinecorePlayer.dll"
Type: files; Name: "{app}\CinecorePlayer.deps.json"
Type: files; Name: "{app}\CinecorePlayer.runtimeconfig.json"
Type: files; Name: "{app}\CinecorePlayer.pdb"
#endif

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Installatori originali di terze parti: visibili, l'utente puo' seguirne le opzioni.
Filename: "{app}\third-parties\installers\LAVFilters-0.80.exe"; StatusMsg: "{cm:StatusComponent,LAV Filters}"; Tasks: lav; Flags: waituntilterminated
Filename: "{app}\third-parties\installers\MPC-Audio-Decoder-x64.exe"; StatusMsg: "{cm:StatusComponent,MPC Audio Decoder}"; Tasks: mpcadec; Flags: waituntilterminated
Filename: "{app}\third-parties\installers\MPC-Audio-Renderer-x64.exe"; StatusMsg: "{cm:StatusComponent,MPC Audio Renderer}"; Tasks: mpcaren; Flags: waituntilterminated
; Filtri lasciati nella cartella del programma: si registrano direttamente (gli script
; originali finiscono con "pause" e resterebbero in attesa).
Filename: "{sys}\regsvr32.exe"; Parameters: "/s ""{app}\third-parties\madVR09217\madVR.ax"""; StatusMsg: "{cm:StatusRegistering}"; Tasks: madvr; Flags: runhidden waituntilterminated
Filename: "{sys}\regsvr32.exe"; Parameters: "/s ""{app}\third-parties\madVR09217\madVR64.ax"""; StatusMsg: "{cm:StatusRegistering}"; Tasks: madvr; Flags: runhidden waituntilterminated
Filename: "{sys}\regsvr32.exe"; Parameters: "/s ""{app}\third-parties\MpcVideoRenderer-0.9.7.2387\MpcVideoRenderer64.ax"""; StatusMsg: "{cm:StatusRegistering}"; Tasks: mpcvr; Flags: runhidden waituntilterminated
Filename: "{sys}\regsvr32.exe"; Parameters: "/s ""{app}\third-parties\XySubFilter_3.1.0.752_x64\XySubFilter.dll"""; StatusMsg: "{cm:StatusRegistering}"; Tasks: xysub; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
; Aggiornamento avviato dal player (/SILENT /RELAUNCH=1): a fine installazione il player riparte,
; con i permessi dell'utente e non con quelli di amministratore del setup.
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser; Check: RelaunchAfterUpdate

[UninstallRun]
Filename: "{app}\third-parties\madVR09217\madHcCtrl.exe"; Parameters: "quit"; Tasks: madvr; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "MadVrQuit"
Filename: "{sys}\regsvr32.exe"; Parameters: "/u /s ""{app}\third-parties\madVR09217\madVR.ax"""; Tasks: madvr; Flags: runhidden waituntilterminated; RunOnceId: "MadVr32"
Filename: "{sys}\regsvr32.exe"; Parameters: "/u /s ""{app}\third-parties\madVR09217\madVR64.ax"""; Tasks: madvr; Flags: runhidden waituntilterminated; RunOnceId: "MadVr64"
Filename: "{sys}\regsvr32.exe"; Parameters: "/u /s ""{app}\third-parties\MpcVideoRenderer-0.9.7.2387\MpcVideoRenderer64.ax"""; Tasks: mpcvr; Flags: runhidden waituntilterminated; RunOnceId: "MpcVr"
Filename: "{sys}\regsvr32.exe"; Parameters: "/u /s ""{app}\third-parties\XySubFilter_3.1.0.752_x64\XySubFilter.dll"""; Tasks: xysub; Flags: runhidden waituntilterminated; RunOnceId: "XySub"

[Code]
function HasWebView2Version(const RootKey: Integer; const SubKey: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function IsWebView2Installed: Boolean;
begin
  Result :=
    HasWebView2Version(HKLM64, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') or
    HasWebView2Version(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') or
    HasWebView2Version(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}');
end;

function RelaunchAfterUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('webview2') then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('StatusWebView2');
    try
      DownloadTemporaryFile('https://go.microsoft.com/fwlink/p/?LinkId=2124703', 'MicrosoftEdgeWebview2Setup.exe', '', nil);
      Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/install', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
    except
      SuppressibleMsgBox(FmtMessage(CustomMessage('WebView2Failed'), [GetExceptionMessage]), mbInformation, MB_OK, IDOK);
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + CustomMessage('FinishedInfo');
end;
