param(
    [switch]$SkipTests,
    [switch]$SkipSmokeTest,
    [string]$VersionOverride = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$NativeRoot = Join-Path $RepoRoot "native-winui"
$SolutionPath = Join-Path $NativeRoot "CaixaMaker.sln"
$AppProjectPath = Join-Path $NativeRoot "src\CaixaMaker.App\CaixaMaker.App.csproj"
$PublishDir = Join-Path $NativeRoot "artifacts\publish\win-x64"
$StagingObjDir = Join-Path $NativeRoot "artifacts\installer-obj"
$ReleaseDir = Join-Path $RepoRoot "release"

$NsisScriptPath = Join-Path $NativeRoot "installer\nsis\CaixaMaker.nsi"
$IconPath = Join-Path $NativeRoot "src\CaixaMaker.App\Assets\icon.ico"
$WixProductPath = Join-Path $NativeRoot "installer\wix\Product.wxs"
$WixLocalePath = Join-Path $NativeRoot "installer\wix\Locale-ptBR.wxl"

function Resolve-ToolPath {
    param(
        [string]$ToolName,
        [string[]]$CandidatePaths
    )

    foreach ($candidate in $CandidatePaths) {
        if ($candidate -and (Test-Path $candidate)) {
            return (Resolve-Path $candidate).Path
        }
    }
    $cmd = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }
    throw "Ferramenta obrigatória não encontrada: $ToolName"
}

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host ("=" * 72) -ForegroundColor Cyan
    Write-Host $Title -ForegroundColor Cyan
    Write-Host ("=" * 72) -ForegroundColor Cyan
}

function Invoke-CheckedStep {
    param(
        [string]$Label,
        [scriptblock]$Action
    )
    Write-Host "-> $Label" -ForegroundColor Gray
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao executar '$Label' (exit code: $LASTEXITCODE)."
    }
}

$PythonExe = Resolve-ToolPath "python.exe" @(
    (Join-Path $RepoRoot ".venv\Scripts\python.exe")
)
$DotNetExe = Resolve-ToolPath "dotnet.exe" @(
    (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"),
    (Join-Path $env:ProgramFiles "dotnet\dotnet.exe")
)
$MakensisExe = Resolve-ToolPath "makensis.exe" @(
    (Join-Path $env:LOCALAPPDATA "tauri\NSIS\makensis.exe"),
    (Join-Path $env:ProgramFiles "NSIS\makensis.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "NSIS\makensis.exe")
)
$WixHeatExe = Resolve-ToolPath "heat.exe" @(
    (Join-Path $env:LOCALAPPDATA "tauri\WixTools314\heat.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "WiX Toolset v3.14\bin\heat.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "WiX Toolset v3.11\bin\heat.exe")
)
$WixCandleExe = Resolve-ToolPath "candle.exe" @(
    (Join-Path $env:LOCALAPPDATA "tauri\WixTools314\candle.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "WiX Toolset v3.14\bin\candle.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "WiX Toolset v3.11\bin\candle.exe")
)
$WixLightExe = Resolve-ToolPath "light.exe" @(
    (Join-Path $env:LOCALAPPDATA "tauri\WixTools314\light.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "WiX Toolset v3.14\bin\light.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "WiX Toolset v3.11\bin\light.exe")
)

# ----------------------------------------------------------------------------
# Etapa 1: Validação SemVer e leitura da versão oficial
# ----------------------------------------------------------------------------
Write-Section "Etapa 1/6: Validação de Versionamento (SemVer)"
$VersionManagerScript = Join-Path $RepoRoot "scripts\version_manager.py"
Invoke-CheckedStep "scripts/version_manager.py --check" {
    & $PythonExe $VersionManagerScript --check
}

$AppVersion = $VersionOverride.Trim()
if ([string]::IsNullOrWhiteSpace($AppVersion)) {
    $AppVersion = (Get-Content (Join-Path $RepoRoot "VERSION") -Raw).Trim()
}
if ($AppVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Versão SemVer inválida para empacotamento MSI/NSIS: '$AppVersion'. Esperado MAJOR.MINOR.PATCH."
}
$BinaryVersion = "$AppVersion.0"
Write-Host "Versão validada: $AppVersion (Assembly/File/VIProductVersion: $BinaryVersion)" -ForegroundColor Green

# ----------------------------------------------------------------------------
# Etapa 2: Suíte completa de testes (.NET 8 Release)
# ----------------------------------------------------------------------------
Write-Section "Etapa 2/6: Compilação e Testes Automatizados (CaixaMaker.Tests)"
if (-not $SkipTests) {
    Invoke-CheckedStep "dotnet test native-winui/CaixaMaker.sln -c Release" {
        & $DotNetExe test $SolutionPath -c Release
    }
} else {
    Write-Host "Aviso: -SkipTests especificado; executando apenas dotnet build -c Release." -ForegroundColor Yellow
    Invoke-CheckedStep "dotnet build native-winui/CaixaMaker.sln -c Release" {
        & $DotNetExe build $SolutionPath -c Release
    }
}

# ----------------------------------------------------------------------------
# Etapa 3: Publicação Self-Contained WinUI 3 Unpackaged (win-x64)
# ----------------------------------------------------------------------------
Write-Section "Etapa 3/6: Publicação Autocontida (win-x64, sem WebView2/Python)"
if (Test-Path $PublishDir) {
    Remove-Item -Recurse -Force $PublishDir
}
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

Invoke-CheckedStep "dotnet publish CaixaMaker.App (win-x64 self-contained)" {
    & $DotNetExe publish $AppProjectPath `
        -c Release `
        -p:PublishProfile=win-x64 `
        -p:PublishDir="$PublishDir\"
}

$PublishedExe = Join-Path $PublishDir "CaixaMaker.exe"
$RequiredPublishedFiles = @(
    "CaixaMaker.exe",
    "CaixaMaker.dll",
    "CaixaMaker.Core.dll",
    "CaixaMaker.Cam.dll",
    "CaixaMaker.Presentation.dll",
    "libSkiaSharp.dll",
    "Microsoft.ui.xaml.dll",
    "resources.pri",
    "Assets\icon.ico"
)
foreach ($rel in $RequiredPublishedFiles) {
    $full = Join-Path $PublishDir $rel
    if (-not (Test-Path $full)) {
        throw "Arquivo obrigatório ausente após dotnet publish: $full"
    }
}

$RemainingPdbs = @(Get-ChildItem -Recurse -File $PublishDir -Filter *.pdb)
if ($RemainingPdbs.Count -gt 0) {
    throw "Arquivos .pdb não deveriam estar presentes no payload publicado: $($RemainingPdbs.Name -join ', ')"
}

$PayloadFiles = @(Get-ChildItem -Recurse -File $PublishDir)
$PayloadBytes = ($PayloadFiles | Measure-Object -Property Length -Sum).Sum
Write-Host ("Payload publicado: {0} arquivos, {1:N0} bytes ({2:N2} MB)" -f $PayloadFiles.Count, $PayloadBytes, ($PayloadBytes / 1MB)) -ForegroundColor Green

# ----------------------------------------------------------------------------
# Etapa 4: Smoke Test no binário publicado (sandbox isolada)
# ----------------------------------------------------------------------------
Write-Section "Etapa 4/6: Smoke Test do Binário Publicado (--smoke-test)"
if (-not $SkipSmokeTest) {
    $SmokeSandbox = Join-Path $env:TEMP ("caixamaker_release_smoke_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $SmokeSandbox | Out-Null

    $PrevEnv = @{
        CAIXA_MAKER_PROJECTS_DIR = $env:CAIXA_MAKER_PROJECTS_DIR
        CAIXA_MAKER_OUTPUTS_DIR = $env:CAIXA_MAKER_OUTPUTS_DIR
        CAIXA_MAKER_CONFIG_DIR = $env:CAIXA_MAKER_CONFIG_DIR
        CAIXA_MAKER_LOGS_DIR = $env:CAIXA_MAKER_LOGS_DIR
        CAIXA_MAKER_TEMP_DIR = $env:CAIXA_MAKER_TEMP_DIR
        CAIXA_MAKER_LEGACY_PROJECTS_DIR = $env:CAIXA_MAKER_LEGACY_PROJECTS_DIR
        CAIXA_MAKER_DESKTOP_MODE = $env:CAIXA_MAKER_DESKTOP_MODE
    }

    try {
        $env:CAIXA_MAKER_PROJECTS_DIR = Join-Path $SmokeSandbox "projects"
        $env:CAIXA_MAKER_OUTPUTS_DIR = Join-Path $SmokeSandbox "outputs"
        $env:CAIXA_MAKER_CONFIG_DIR = Join-Path $SmokeSandbox "config"
        $env:CAIXA_MAKER_LOGS_DIR = Join-Path $SmokeSandbox "logs"
        $env:CAIXA_MAKER_TEMP_DIR = Join-Path $SmokeSandbox "temp"
        $env:CAIXA_MAKER_LEGACY_PROJECTS_DIR = Join-Path $SmokeSandbox "legacy_empty"
        $env:CAIXA_MAKER_DESKTOP_MODE = "1"

        $proc = Start-Process -FilePath $PublishedExe `
            -ArgumentList "--smoke-test" `
            -WorkingDirectory $PublishDir `
            -Wait `
            -PassThru `
            -NoNewWindow

        if ($proc.ExitCode -ne 0) {
            throw "CaixaMaker.exe --smoke-test falhou com exit code $($proc.ExitCode)."
        }

        $LogFile = Join-Path $env:CAIXA_MAKER_LOGS_DIR "desktop-native.log"
        if (-not (Test-Path $LogFile)) {
            throw "Arquivo de log esperado não encontrado após smoke test: $LogFile"
        }
        $LogContent = Get-Content $LogFile -Raw -Encoding UTF8
        if ($LogContent -match '\[ERROR\]') {
            throw "Log do smoke test contém erro(s):`n$LogContent"
        }
        if ($LogContent -notmatch 'Viewport3D/2D e Fabricação verificados') {
            throw "Log do smoke test não confirmou etapa Viewport3D/2D e Fabricação:`n$LogContent"
        }
        Write-Host "Smoke test concluído com EXIT_CODE=0 e zero erros no log." -ForegroundColor Green
    } finally {
        foreach ($entry in $PrevEnv.GetEnumerator()) {
            if ($null -eq $entry.Value) {
                Remove-Item ("Env:" + $entry.Key) -ErrorAction SilentlyContinue
            } else {
                Set-Item ("Env:" + $entry.Key) $entry.Value
            }
        }
        if (Test-Path $SmokeSandbox) {
            Remove-Item -Recurse -Force $SmokeSandbox -ErrorAction SilentlyContinue
        }
    }
} else {
    Write-Host "Aviso: -SkipSmokeTest especificado; etapa pulada." -ForegroundColor Yellow
}

# ----------------------------------------------------------------------------
# Etapa 5: Empacotamento NSIS (.exe) e WiX Toolset (.msi)
# ----------------------------------------------------------------------------
Write-Section "Etapa 5/6: Geração dos Instaladores Nativos NSIS (.exe) e WiX (.msi)"
if (Test-Path $StagingObjDir) {
    Remove-Item -Recurse -Force $StagingObjDir
}
New-Item -ItemType Directory -Force -Path $StagingObjDir | Out-Null
New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null

$SetupVersionedName = "CaixaMaker-Setup-$AppVersion-x64.exe"
$SetupLatestName = "CaixaMaker-Setup.exe"
$MsiVersionedName = "CaixaMaker-$AppVersion-x64.msi"

$StagingNsisExe = Join-Path $StagingObjDir $SetupVersionedName
$StagingMsi = Join-Path $StagingObjDir $MsiVersionedName

# 5A. Gerar listas cirúrgicas de arquivos e diretórios para o NSIS
$NsisInstallFilesNsh = Join-Path $StagingObjDir "nsis-install-files.nsh"
$NsisUninstallFilesNsh = Join-Path $StagingObjDir "nsis-uninstall-files.nsh"

$PublishNormRoot = $PublishDir.TrimEnd('\', '/')
$FilesByRelDir = [System.Collections.Generic.SortedDictionary[string, System.Collections.Generic.List[string]]]::new([System.StringComparer]::OrdinalIgnoreCase)

foreach ($file in $PayloadFiles) {
    $parentDir = $file.DirectoryName.TrimEnd('\', '/')
    $relDir = ""
    if ($parentDir.Length -gt $PublishNormRoot.Length) {
        $relDir = $parentDir.Substring($PublishNormRoot.Length + 1)
    }
    if (-not $FilesByRelDir.ContainsKey($relDir)) {
        $FilesByRelDir[$relDir] = [System.Collections.Generic.List[string]]::new()
    }
    $FilesByRelDir[$relDir].Add($file.FullName)
}

$InstallLines = [System.Collections.Generic.List[string]]::new()
$UninstallLines = [System.Collections.Generic.List[string]]::new()
$DirsForRemoval = [System.Collections.Generic.List[string]]::new()

foreach ($kvp in $FilesByRelDir.GetEnumerator()) {
    $relDir = $kvp.Key
    if ([string]::IsNullOrEmpty($relDir)) {
        $InstallLines.Add('SetOutPath "$INSTDIR"')
    } else {
        $InstallLines.Add("SetOutPath `"`$INSTDIR\$relDir`"")
        $DirsForRemoval.Add($relDir)
    }
    foreach ($fullPath in ($kvp.Value | Sort-Object)) {
        $fileName = [System.IO.Path]::GetFileName($fullPath)
        $InstallLines.Add("File `"$fullPath`"")
        if ([string]::IsNullOrEmpty($relDir)) {
            $UninstallLines.Add("Delete `"`$INSTDIR\$fileName`"")
        } else {
            $UninstallLines.Add("Delete `"`$INSTDIR\$relDir\$fileName`"")
        }
    }
}

# Remove subdiretórios dos mais profundos para os mais rasos
$SortedDirsToRemove = $DirsForRemoval | Sort-Object { ($_.Split('\').Count) } -Descending
foreach ($relDir in $SortedDirsToRemove) {
    $UninstallLines.Add("RMDir `"`$INSTDIR\$relDir`"")
}

$Utf8Bom = [System.Text.UTF8Encoding]::new($true)
[System.IO.File]::WriteAllLines($NsisInstallFilesNsh, $InstallLines, $Utf8Bom)
[System.IO.File]::WriteAllLines($NsisUninstallFilesNsh, $UninstallLines, $Utf8Bom)

# Copiamos temporariamente NSI/NSH garantindo UTF-8 e invocamos makensis com /INPUTCHARSET UTF8
Invoke-CheckedStep "makensis.exe -> $SetupVersionedName" {
    & $MakensisExe `
        /INPUTCHARSET UTF8 `
        /DVERSION="$AppVersion" `
        /DBINARY_VERSION="$BinaryVersion" `
        /DPAYLOAD_DIR="$PublishDir" `
        /DICON_PATH="$IconPath" `
        /DOUT_FILE="$StagingNsisExe" `
        /DINSTALL_FILES_NSH="$NsisInstallFilesNsh" `
        /DUNINSTALL_FILES_NSH="$NsisUninstallFilesNsh" `
        $NsisScriptPath
}

if (-not (Test-Path $StagingNsisExe)) {
    throw "Instalador NSIS não foi gerado em: $StagingNsisExe"
}

# 5B. WiX Toolset (heat + filtragem XML + candle + light)
$WixPayloadWxs = Join-Path $StagingObjDir "AppPayload.wxs"
Invoke-CheckedStep "heat.exe (coleta automática do payload WinUI 3)" {
    & $WixHeatExe dir "$PublishDir" `
        -cg AppPayloadGroup `
        -ag `
        -scom `
        -sreg `
        -sfrag `
        -srd `
        -dr INSTALLDIR `
        -var var.PayloadDir `
        -out "$WixPayloadWxs"
}

# Remove componente duplicado de CaixaMaker.exe do AppPayload.wxs (já declarado em MainExecutableComponent)
[xml]$HeatXml = Get-Content $WixPayloadWxs -Raw -Encoding UTF8
$NsMgr = [System.Xml.XmlNamespaceManager]::new($HeatXml.NameTable)
$NsMgr.AddNamespace("wix", "http://schemas.microsoft.com/wix/2006/wi")

$ExeFileNodes = $HeatXml.SelectNodes("//wix:File[@Source='`$(var.PayloadDir)\CaixaMaker.exe']", $NsMgr)
if ($ExeFileNodes.Count -ne 1) {
    throw "Esperado exatamente 1 nó CaixaMaker.exe em AppPayload.wxs; encontrados: $($ExeFileNodes.Count)"
}
$ExeCompNode = $ExeFileNodes[0].ParentNode
$ExeCompId = $ExeCompNode.GetAttribute("Id")
$ExeCompNode.ParentNode.RemoveChild($ExeCompNode) | Out-Null

$ExeRefNodes = $HeatXml.SelectNodes("//wix:ComponentRef[@Id='$ExeCompId']", $NsMgr)
foreach ($refNode in $ExeRefNodes) {
    $refNode.ParentNode.RemoveChild($refNode) | Out-Null
}
$HeatXml.Save($WixPayloadWxs)

$WixObjDir = Join-Path $StagingObjDir "wixobj"
New-Item -ItemType Directory -Force -Path $WixObjDir | Out-Null

Invoke-CheckedStep "candle.exe (compilação WiX x64)" {
    & $WixCandleExe `
        -nologo `
        -arch x64 `
        -ext WixUIExtension `
        -ext WixUtilExtension `
        "-dVersion=$AppVersion" `
        "-dPayloadDir=$PublishDir" `
        "-dIconPath=$IconPath" `
        -out "$WixObjDir\\" `
        $WixProductPath `
        $WixPayloadWxs
}

$MainWixObj = Join-Path $WixObjDir "Product.wixobj"
$PayloadWixObj = Join-Path $WixObjDir "AppPayload.wixobj"

# Suprime ICE03 especificamente para multi-LCID/LCIDs Win11 na tabela File de DLLs/MUI nativos do Windows App SDK (darice.cub legado do WiX 3.14)
Invoke-CheckedStep "light.exe -> $MsiVersionedName (linkagem MSI pt-BR)" {
    & $WixLightExe `
        -nologo `
        -sice:ICE03 `
        -ext WixUIExtension `
        -ext WixUtilExtension `
        -cultures:pt-br `
        -loc $WixLocalePath `
        -out $StagingMsi `
        $MainWixObj `
        $PayloadWixObj
}

if (-not (Test-Path $StagingMsi)) {
    throw "Instalador MSI não foi gerado em: $StagingMsi"
}

# ----------------------------------------------------------------------------
# Etapa 6: Publicação final em release/ e relatório auditável
# ----------------------------------------------------------------------------
Write-Section "Etapa 6/6: Publicação em release/ e Relatório Final"

$FinalSetupVersioned = Join-Path $ReleaseDir $SetupVersionedName
$FinalSetupLatest = Join-Path $ReleaseDir $SetupLatestName
$FinalMsiVersioned = Join-Path $ReleaseDir $MsiVersionedName

Copy-Item -Force $StagingNsisExe $FinalSetupVersioned
Copy-Item -Force $StagingNsisExe $FinalSetupLatest
Copy-Item -Force $StagingMsi $FinalMsiVersioned

$Artifacts = @(
    $FinalSetupLatest,
    $FinalSetupVersioned,
    $FinalMsiVersioned
)

Write-Host ""
Write-Host "Instaladores nativos gerados com sucesso em ${ReleaseDir}:" -ForegroundColor Green
$Report = foreach ($artifactPath in $Artifacts) {
    $item = Get-Item $artifactPath
    $hash = (Get-FileHash -Algorithm SHA256 $artifactPath).Hash
    [pscustomobject]@{
        Arquivo = $item.Name
        Bytes = $item.Length
        MB = [math]::Round($item.Length / 1MB, 2)
        SHA256 = $hash
    }
}
$Report | Format-Table -AutoSize

Write-Host "Pipeline de release nativo concluído com êxito." -ForegroundColor Green
