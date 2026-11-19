; Instalador Nativo Windows (NSIS Unicode x64) - Caixa Maker (WinUI 3 Self-Contained)
; Sem dependências externas de runtime ou navegador embutido.

Unicode true
ManifestDPIAware true
ManifestDPIAwareness PerMonitorV2
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"

!ifndef VERSION
  !define VERSION "2.4.0"
!endif
!ifndef BINARY_VERSION
  !define BINARY_VERSION "2.4.0.0"
!endif
!ifndef PAYLOAD_DIR
  !define PAYLOAD_DIR "..\..\artifacts\publish\win-x64"
!endif
!ifndef ICON_PATH
  !define ICON_PATH "..\..\src\CaixaMaker.App\Assets\icon.ico"
!endif
!ifndef OUT_FILE
  !define OUT_FILE "..\..\..\release\CaixaMaker-Setup-${VERSION}-x64.exe"
!endif

!define PRODUCT_NAME "Caixa Maker"
!define PRODUCT_PUBLISHER "Laboratório IFMaker do Juruá — IFAC"
!define PRODUCT_DESCRIPTION "Caixa Maker - Modelagem Paramétrica e Preparação CNC para MDF"
!define MAIN_EXE "CaixaMaker.exe"
!define LEGACY_EXE "caixa-maker-desktop.exe"
!define APP_REG_KEY "Software\${PRODUCT_NAME}"
!define UNINST_REG_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}"

Name "${PRODUCT_NAME}"
OutFile "${OUT_FILE}"
InstallDir "$LOCALAPPDATA\${PRODUCT_NAME}"
InstallDirRegKey HKCU "${APP_REG_KEY}" "InstallLocation"
RequestExecutionLevel user
ShowInstDetails show
ShowUnInstDetails show

; Metadados de versão PE do instalador
VIProductVersion "${BINARY_VERSION}"
VIAddVersionKey /LANG=1046 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1046 "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=1046 "FileDescription" "${PRODUCT_DESCRIPTION}"
VIAddVersionKey /LANG=1046 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1046 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1046 "Comments" "Desenvolvido por Pedro G. Cruz (PddroGCruz • ggcostcr@gmail.com)"
VIAddVersionKey /LANG=1046 "LegalCopyright" "Copyright © 2026 ${PRODUCT_PUBLISHER}"

; Configuração Modern UI 2
!define MUI_ICON "${ICON_PATH}"
!define MUI_UNICON "${ICON_PATH}"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${MAIN_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "$(CM_RunAfterInstall)"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "PortugueseBR"
!include "PortugueseBR.nsh"

!macro EnsureAppClosed processName
  nsExec::ExecToStack 'cmd /C tasklist /FI "IMAGENAME eq ${processName}" /NH | findstr /I "${processName}"'
  Pop $0
  Pop $1
  ${If} $0 == 0
    IfSilent +3 0
      MessageBox MB_YESNO|MB_ICONEXCLAMATION "$(CM_AppRunningMsg)" IDYES +2
      Abort
    nsExec::ExecToStack 'taskkill /F /T /IM "${processName}"'
    Pop $0
    Pop $1
    Sleep 600
    nsExec::ExecToStack 'cmd /C tasklist /FI "IMAGENAME eq ${processName}" /NH | findstr /I "${processName}"'
    Pop $0
    Pop $1
    ${If} $0 == 0
      IfSilent 0 +2
        Abort
      MessageBox MB_OK|MB_ICONSTOP "$(CM_CloseFailedMsg)"
      Abort
    ${EndIf}
  ${EndIf}
!macroend

Function .onInit
  SetRegView 64
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "Este instalador requer Windows x64 (64 bits)."
    Abort
  ${EndIf}
FunctionEnd

Function un.onInit
  SetRegView 64
  !insertmacro EnsureAppClosed "${MAIN_EXE}"
FunctionEnd

Section "Caixa Maker" SecMain
  SectionIn RO
  SetRegView 64

  !insertmacro EnsureAppClosed "${MAIN_EXE}"
  !insertmacro EnsureAppClosed "${LEGACY_EXE}"

  DetailPrint "$(CM_InstallingMsg)"
  SetOutPath "$INSTDIR"

  ; Remove binário legado da arquitetura anterior, caso exista no mesmo diretório de instalação
  IfFileExists "$INSTDIR\${LEGACY_EXE}" 0 +2
    Delete "$INSTDIR\${LEGACY_EXE}"

  !ifdef INSTALL_FILES_NSH
    !include "${INSTALL_FILES_NSH}"
  !else
    File /r "${PAYLOAD_DIR}\*.*"
  !endif

  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Atalhos do Menu Iniciar e Área de Trabalho com ícone institucional
  CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\${MAIN_EXE}" "" "$INSTDIR\Assets\icon.ico" 0
  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\Desinstalar ${PRODUCT_NAME}.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Assets\icon.ico" 0
  CreateShortCut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${MAIN_EXE}" "" "$INSTDIR\Assets\icon.ico" 0

  ; Cálculo real do tamanho instalado em KB para Programas e Recursos
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0

  ; Registro de instalação e desinstalação (sem remover dados do usuário em %APPDATA%\CaixaMaker)
  WriteRegStr HKCU "${APP_REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_REG_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${UNINST_REG_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST_REG_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKCU "${UNINST_REG_KEY}" "DisplayIcon" "$INSTDIR\Assets\icon.ico"
  WriteRegStr HKCU "${UNINST_REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_REG_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_REG_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINST_REG_KEY}" "EstimatedSize" "$0"
  WriteRegDWORD HKCU "${UNINST_REG_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_REG_KEY}" "NoRepair" 1

  ; Associação nativa do formato .cmaker no Windows Explorer (HKCU sem necessidade de elevação)
  WriteRegStr HKCU "Software\Classes\.cmaker" "" "CaixaMaker.Project"
  WriteRegStr HKCU "Software\Classes\.cmaker" "Content Type" "application/x-caixamaker-project"
  WriteRegStr HKCU "Software\Classes\CaixaMaker.Project" "" "Projeto Caixa Maker (.cmaker)"
  WriteRegStr HKCU "Software\Classes\CaixaMaker.Project\DefaultIcon" "" "$INSTDIR\Assets\icon.ico,0"
  WriteRegStr HKCU "Software\Classes\CaixaMaker.Project\shell" "" "open"
  WriteRegStr HKCU "Software\Classes\CaixaMaker.Project\shell\open" "" "Abrir no Caixa Maker"
  WriteRegStr HKCU "Software\Classes\CaixaMaker.Project\shell\open\command" "" '"$INSTDIR\${MAIN_EXE}" "%1"'
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd

Section "Uninstall"
  SetRegView 64
  DetailPrint "$(CM_UninstallNote)"

  !ifdef UNINSTALL_FILES_NSH
    !include "${UNINSTALL_FILES_NSH}"
  !else
    Delete "$INSTDIR\${MAIN_EXE}"
    Delete "$INSTDIR\*.dll"
    Delete "$INSTDIR\*.json"
    Delete "$INSTDIR\*.pri"
    Delete "$INSTDIR\*.xbf"
    Delete "$INSTDIR\*.mui"
    RMDir /r "$INSTDIR\Assets"
  !endif

  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  ; Remoção de atalhos criados pelo instalador
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\Desinstalar ${PRODUCT_NAME}.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT_NAME}"
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"

  ; Limpeza do registro (dados em %APPDATA%\CaixaMaker são intencionalmente preservados)
  DeleteRegKey HKCU "${UNINST_REG_KEY}"
  DeleteRegKey HKCU "${APP_REG_KEY}"
  DeleteRegKey HKCU "Software\Classes\.cmaker"
  DeleteRegKey HKCU "Software\Classes\CaixaMaker.Project"
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd
