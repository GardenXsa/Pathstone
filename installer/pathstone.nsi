; ============================================================================
; WIN-INSTALLER (issue #4): NSIS installer script for Pathstone.
;
; Produces `Pathstone-Setup-0.2.0.exe` — a Windows installer that
; bundles the self-contained win-x64 publish output (see
; `scripts/build-windows.sh` / `scripts/build-windows.ps1`) into a
; single .exe with Start Menu + Desktop shortcuts, an uninstaller,
; and file associations for `.pathstone-world` / `.pathstone-char`.
;
; ----------------------------------------------------------------------------
; Build:
;   1. Run `scripts/build-windows.sh` (or .ps1) to populate
;      `publish/win-x64/`.
;   2. Run `makensis installer/pathstone.nsi` to produce
;      `installer/Pathstone-Setup-0.2.0.exe`.
;
; ----------------------------------------------------------------------------
; Install location: $LOCALAPPDATA\Pathstone (per-user, no admin
; rights required — `RequestExecutionLevel user`). This avoids UAC
; prompts at install time. To install for all users instead, change
; to `$PROGRAMFILES\Pathstone` and set `RequestExecutionLevel admin`.
;
; ----------------------------------------------------------------------------
; The installer is unsigned (see closed #56). Windows SmartScreen
; will show a warning on first run — users click "More info" →
; "Run anyway".
;
; Version is sourced from `MyGame.Core.Common.Version.Current`
; (currently "0.2.0"). Bump both this `!define VERSION` and the
; Core constant in lockstep when releasing a new version.
; ============================================================================

!define APPNAME           "Pathstone"
!define APPNAME_FRIENDLY  "Pathstone"
!define PUBLISHER         "Pathstone"
!define VERSION           "0.2.0"
!define EXE_NAME          "MyGame.Desktop.exe"
!define UNINST_EXE        "Uninstall.exe"
!define REGKEY            "Software\Pathstone"
!define UNINSTKEY         "Software\Microsoft\Windows\CurrentVersion\Uninstall\Pathstone"

; ----------------------------------------------------------------------------
; Modern UI 2 ( nicer wizard chrome — pretty much the standard
; NSIS look). Bundled with NSIS 3.x.
; ----------------------------------------------------------------------------
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

; ----------------------------------------------------------------------------
; Installer metadata (shown in the .exe file properties dialog).
; ----------------------------------------------------------------------------
Name              "${APPNAME_FRIENDLY}"
OutFile           "Pathstone-Setup-${VERSION}.exe"
InstallDir        "$LOCALAPPDATA\Pathstone"
InstallDirRegKey  HKCU "${REGKEY}" "InstallDir"
ShowInstDetails   show
ShowUnInstDetails show
RequestExecutionLevel user
Unicode           true
SetCompressor     /SOLID lzma

; ----------------------------------------------------------------------------
; Version info embedded in the .exe (right-click → Properties →
; Details). VIProductVersion requires an X.X.X.X numeric tuple —
; we pad the 3-part semver with a trailing .0.
; ----------------------------------------------------------------------------
VIAddVersionKey "ProductName"      "${APPNAME_FRIENDLY}"
VIAddVersionKey "FileDescription"  "${APPNAME_FRIENDLY} Setup"
VIAddVersionKey "CompanyName"      "${PUBLISHER}"
VIAddVersionKey "LegalCopyright"   "© ${PUBLISHER}"
VIAddVersionKey "FileVersion"      "${VERSION}.0"
VIAddVersionKey "ProductVersion"   "${VERSION}.0"
VIProductVersion "${VERSION}.0"

; ----------------------------------------------------------------------------
; MUI pages: welcome → license-less → install dir → components →
; install → finish. The Desktop-shortcut checkbox lives on the
; components page (the section is marked optional).
; ----------------------------------------------------------------------------
!define MUI_ICON   "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; English + Russian (the UI ships Russian strings — see README).
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Russian"

; ============================================================================
; Sections
; ============================================================================

; ----------------------------------------------------------------------------
; Core files (required). Mirrors `publish/win-x64/*` into the
; install directory.
; ----------------------------------------------------------------------------
Section "${APPNAME_FRIENDLY} (required)" SecCore
  SectionIn RO  ; read-only — always installed, can't be unchecked

  SetOutPath "$INSTDIR"
  File /r "..\publish\win-x64\*.*"

  ; Remember the install dir for upgrades / uninstall.
  WriteRegStr HKCU "${REGKEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${REGKEY}" "Version"    "${VERSION}"

  ; Add/Remove Programs entry. Uses HKCU (per-user install); the
  ; uninstaller path + display name + version + publisher + install
  ; location + estimated size are all set here so the Windows
  ; Settings → Apps list shows the right metadata.
  WriteRegStr   HKCU "${UNINSTKEY}" "DisplayName"     "${APPNAME_FRIENDLY}"
  WriteRegStr   HKCU "${UNINSTKEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr   HKCU "${UNINSTKEY}" "Publisher"       "${PUBLISHER}"
  WriteRegStr   HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKCU "${UNINSTKEY}" "DisplayIcon"     "$INSTDIR\${EXE_NAME}"
  WriteRegStr   HKCU "${UNINSTKEY}" "UninstallString" "$\"$INSTDIR\${UNINST_EXE}$\""
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1

  ; Estimate the install size (KB) for the Settings → Apps list.
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" "$0"

  ; Write the uninstaller.
  WriteUninstaller "$INSTDIR\${UNINST_EXE}"
SectionEnd

; ----------------------------------------------------------------------------
; Start Menu shortcut (optional, checked by default).
; ----------------------------------------------------------------------------
Section "Start Menu shortcut" SecStartMenu
  CreateDirectory "$SMPROGRAMS\${APPNAME_FRIENDLY}"
  CreateShortcut  "$SMPROGRAMS\${APPNAME_FRIENDLY}\${APPNAME_FRIENDLY}.lnk" \
                  "$INSTDIR\${EXE_NAME}" "" \
                  "$INSTDIR\${EXE_NAME}" 0 \
                  "" "" "" "${APPNAME_FRIENDLY}"
  CreateShortcut  "$SMPROGRAMS\${APPNAME_FRIENDLY}\Uninstall ${APPNAME_FRIENDLY}.lnk" \
                  "$INSTDIR\${UNINST_EXE}" "" \
                  "$INSTDIR\${UNINST_EXE}" 0
SectionEnd

; ----------------------------------------------------------------------------
; Desktop shortcut (optional, unchecked by default).
; ----------------------------------------------------------------------------
Section "Desktop shortcut" SecDesktop
  CreateShortcut "$DESKTOP\${APPNAME_FRIENDLY}.lnk" \
                 "$INSTDIR\${EXE_NAME}" "" \
                 "$INSTDIR\${EXE_NAME}" 0 \
                 "" "" "" "${APPNAME_FRIENDLY}"
SectionEnd

; ----------------------------------------------------------------------------
; File associations for `.pathstone-world` and `.pathstone-char`.
; The app doesn't handle file args yet — this just registers the
; association for future use, so double-clicking a save file will
; launch Pathstone (and the app can later parse the path from
; `Environment.GetCommandLineArgs`).
; ----------------------------------------------------------------------------
Section "Associate .pathstone-world / .pathstone-char files" SecFileAssoc
  ; .pathstone-world
  WriteRegStr HKCU "Software\Classes\.pathstone-world"  "" "Pathstone.World"
  WriteRegStr HKCU "Software\Classes\Pathstone.World"   "" "Pathstone World Save"
  WriteRegStr HKCU "Software\Classes\Pathstone.World\DefaultIcon" "" "$INSTDIR\${EXE_NAME},0"
  WriteRegStr HKCU "Software\Classes\Pathstone.World\shell\open\command" "" '$\"$INSTDIR\${EXE_NAME}$\" "$\"%1$\""'

  ; .pathstone-char
  WriteRegStr HKCU "Software\Classes\.pathstone-char"   "" "Pathstone.Character"
  WriteRegStr HKCU "Software\Classes\Pathstone.Character" "" "Pathstone Character Sheet"
  WriteRegStr HKCU "Software\Classes\Pathstone.Character\DefaultIcon" "" "$INSTDIR\${EXE_NAME},0"
  WriteRegStr HKCU "Software\Classes\Pathstone.Character\shell\open\command" "" '$\"$INSTDIR\${EXE_NAME}$\" "$\"%1$\""'

  ; Notify the shell that file associations changed.
  System::Call 'shell32.dll::SHChangeNotify(i 0x08000000, i 0, i 0, i 0)'
SectionEnd

; ----------------------------------------------------------------------------
; Section descriptions (shown on the components page when the user
; hovers a section).
; ----------------------------------------------------------------------------
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCore}       "Pathstone application files (required)."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecStartMenu}  "Create a Start Menu shortcut."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop}    "Create a Desktop shortcut."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecFileAssoc}  "Open .pathstone-world and .pathstone-char files with Pathstone."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ============================================================================
; Uninstall
; ============================================================================
Section "Uninstall"
  ; Remove files. We delete the whole install directory (the
  ; publish output is fully owned by the installer; the only
  ; user-generated files there are logs in a subfolder we'll also
  ; clean up). Saves and settings live in %APPDATA%\Pathstone, not
  ; under $INSTDIR, so they survive uninstall.
  RMDir /r "$INSTDIR"
  RMDir /r "$SMPROGRAMS\${APPNAME_FRIENDLY}"
  Delete "$DESKTOP\${APPNAME_FRIENDLY}.lnk"

  ; Clean registry.
  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "${REGKEY}"
  DeleteRegKey HKCU "Software\Classes\.pathstone-world"
  DeleteRegKey HKCU "Software\Classes\Pathstone.World"
  DeleteRegKey HKCU "Software\Classes\.pathstone-char"
  DeleteRegKey HKCU "Software\Classes\Pathstone.Character"

  System::Call 'shell32.dll::SHChangeNotify(i 0x08000000, i 0, i 0, i 0)'
SectionEnd

; ============================================================================
; Callbacks
; ============================================================================

Function .onInit
  ; Default check state for optional sections.
  SectionSetFlags ${SecStartMenu}  1  ; checked
  SectionSetFlags ${SecDesktop}    0  ; unchecked
  SectionSetFlags ${SecFileAssoc}  1  ; checked
FunctionEnd
