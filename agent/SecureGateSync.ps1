<#
.SYNOPSIS
  통합 에이전트 — QR 업로드 서버에서 '내 토큰' 파일을 자동으로 당겨와(pull)
  SecureGate 전송 대기 목록에 자동으로 얹는다(=프로그램2b+2 통합).

.DESCRIPTION
  반복 동작:
    1) GET {Server}/u/{Token}/list                → 내 파일 목록 (UTF-8로 직접 디코딩)
    2) GET {Server}/u/{Token}/file/<name>         → WorkFolder 로 다운로드(.part→rename)
    3) DELETE .../file/<name>                      → 서버에서 삭제(소비형)
    4) 이번에 받은 파일이 있으면 UTF-16LE+BOM 목록 txt 생성 후
       SecureGate.exe F <개수> <목록경로> (따옴표 없음) 실행 → 전송 대기 목록에 얹힘
    5) IntervalSeconds 대기 후 반복

  ★ '목록에 얹기'까지만 자동. 실제 전송(보내기)은 사용자가 SecureGate 창에서 직접 클릭.

.NOTES
  Windows PowerShell 5.1 호환. 설정은 같은 폴더의 SecureGateSync.config.psd1.
  보통 개인별 설치파일(.cmd)이 이 스크립트와 설정을 자동 배치하고 작업 스케줄러에 등록한다.
#>

[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$ServerBaseUrl,
    [string]$Token,
    [int]$IntervalSeconds,
    [string]$SecureGateExe,
    [string]$WorkFolder,        # 받은 사진을 두는 폴더 (SecureGate가 전송 시 읽음)
    [string]$ListOutputFolder,  # 목록 txt 생성 폴더
    [string]$LogFile,
    [switch]$SkipCertCheck,
    [switch]$DryRun,            # SecureGate 실행 대신 로그만
    [switch]$Once               # 1회만 실행 후 종료 (테스트용)
)

$ScriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ConfigPath) { $ConfigPath = Join-Path $ScriptDir 'SecureGateSync.config.psd1' }

$bound  = $PSBoundParameters
$config = @{}
if ($ConfigPath -and (Test-Path -LiteralPath $ConfigPath)) {
    try { $config = Import-PowerShellDataFile -Path $ConfigPath } catch { Write-Warning "설정 로드 실패: $($_.Exception.Message)"; $config = @{} }
}
function Resolve-Cfg { param($Name, $Default)
    if ($bound.ContainsKey($Name))  { return (Get-Variable -Name $Name -ValueOnly) }
    if ($config.ContainsKey($Name)) { return $config[$Name] }
    return $Default
}

$ServerBaseUrl    = ("" + (Resolve-Cfg 'ServerBaseUrl' 'https://YOUR-APP.onrender.com')).TrimEnd('/')
$Token            = Resolve-Cfg 'Token' ''
$IntervalSeconds  = [int](Resolve-Cfg 'IntervalSeconds' 3)
$SecureGateExe    = Resolve-Cfg 'SecureGateExe' 'C:\HANSSAK\SecureGateEX\SecureGate.exe'
$WorkFolder       = Resolve-Cfg 'WorkFolder' (Join-Path $env:LOCALAPPDATA 'SecureGateSync\incoming')
$ListOutputFolder = Resolve-Cfg 'ListOutputFolder' (Join-Path $env:USERPROFILE 'AppData\LocalLow\HANSSAK\RList')
$LogFile          = Resolve-Cfg 'LogFile' (Join-Path $ScriptDir 'logs\sync.log')
$SkipCertCheck    = [bool](Resolve-Cfg 'SkipCertCheck' $false)
$DryRun           = [bool](Resolve-Cfg 'DryRun' $false)
if ($IntervalSeconds -lt 1) { $IntervalSeconds = 1 }

# ── 함수 ────────────────────────────────────────────────────────────
function Write-Log { param([ValidateSet('INFO','WARN','ERROR')][string]$Level='INFO', [string]$Message)
    $ts=(Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); $line="[$ts] [$Level] $Message"
    switch ($Level) { 'ERROR'{Write-Host $line -ForegroundColor Red} 'WARN'{Write-Host $line -ForegroundColor Yellow} default{Write-Host $line} }
    try { $d=Split-Path -Parent $LogFile; if($d -and -not(Test-Path -LiteralPath $d)){New-Item -ItemType Directory -Path $d -Force|Out-Null}
          Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8 } catch {}
}
function Get-UniqueDest { param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $Path }
    $dir=Split-Path -Parent $Path; $b=[IO.Path]::GetFileNameWithoutExtension($Path); $e=[IO.Path]::GetExtension($Path); $i=1
    do { $c=Join-Path $dir ("{0}({1}){2}" -f $b,$i,$e); $i++ } while (Test-Path -LiteralPath $c); return $c
}
function New-ListFile { param([string[]]$Paths)
    if (-not (Test-Path -LiteralPath $ListOutputFolder)) { New-Item -ItemType Directory -Path $ListOutputFolder -Force | Out-Null }
    $stamp=(Get-Date).ToString('yyyyMMddHHmmss'); $lp=Join-Path $ListOutputFolder ($stamp+'.txt'); $i=1
    while (Test-Path -LiteralPath $lp) { $lp=Join-Path $ListOutputFolder ("{0}_{1}.txt" -f $stamp,$i); $i++ }
    $body=($Paths -join "`r`n")+"`r`n"
    [IO.File]::WriteAllText($lp, $body, [Text.Encoding]::Unicode)   # UTF-16LE + BOM
    return $lp
}
function Invoke-SecureGate { param([string[]]$Paths)
    $count=$Paths.Count
    $lp=New-ListFile -Paths $Paths
    $argStr = 'F {0} {1}' -f $count, $lp        # ★ 목록경로 따옴표 없이 (SecureGate가 따옴표 안 벗김)
    if ($DryRun) {
        Write-Log INFO "[DryRun] 실행안함: `"$SecureGateExe`" $argStr"
        foreach($p in $Paths){ Write-Log INFO "[DryRun]   - $p" }
        return
    }
    if (-not (Test-Path -LiteralPath $SecureGateExe)) { Write-Log ERROR "SecureGate 실행파일 없음: $SecureGateExe (목록: $lp)"; return }
    try { Start-Process -FilePath $SecureGateExe -ArgumentList $argStr -ErrorAction Stop | Out-Null
          Write-Log INFO "SecureGate 목록 투입: F $count $lp ($count개)" }
    catch { Write-Log ERROR "SecureGate 실행 실패: $($_.Exception.Message)" }
}

function Invoke-SyncOnce {
    # 1) 목록 조회 (PS5.1 JSON UTF-8 이슈 → RawContentStream 직접 디코딩)
    $listUrl="$ServerBaseUrl/u/$Token/list"
    try {
        $raw=Invoke-WebRequest -Uri $listUrl -Method Get -TimeoutSec 30 -UseBasicParsing
        $text=[Text.Encoding]::UTF8.GetString($raw.RawContentStream.ToArray())
        $resp=$text | ConvertFrom-Json
    } catch { Write-Log WARN "목록 조회 실패: $($_.Exception.Message)"; return }
    $files=@($resp.files)
    if ($files.Count -eq 0) { return }
    Write-Log INFO "새 파일 $($files.Count)개 → 다운로드"

    if (-not (Test-Path -LiteralPath $WorkFolder)) { New-Item -ItemType Directory -Path $WorkFolder -Force | Out-Null }
    $got=@()
    foreach ($f in $files) {
        $name="$($f.name)"; if (-not $name) { continue }
        $enc=[uri]::EscapeDataString($name); $fileUrl="$ServerBaseUrl/u/$Token/file/$enc"
        $final=Get-UniqueDest (Join-Path $WorkFolder $name); $part="$final.part"
        try {
            Invoke-WebRequest -Uri $fileUrl -OutFile $part -Method Get -TimeoutSec 120 -UseBasicParsing
            Rename-Item -LiteralPath $part -NewName ([IO.Path]::GetFileName($final)) -ErrorAction Stop
            $got += $final
            try { Invoke-RestMethod -Uri $fileUrl -Method Delete -TimeoutSec 30 | Out-Null } catch { Write-Log WARN "서버삭제 실패: $name" }
        } catch {
            Write-Log ERROR "다운로드 실패: $name ($($_.Exception.Message))"
            if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue }
        }
    }
    # 2) 받은 파일 SecureGate 목록에 투입
    if ($got.Count -gt 0) { Invoke-SecureGate -Paths $got }
}

# ── 초기화 ──────────────────────────────────────────────────────────
if ($SkipCertCheck) {
    try { Add-Type @"
using System.Net; using System.Security.Cryptography.X509Certificates;
public class TrustAll2 : ICertificatePolicy { public bool CheckValidationResult(ServicePoint s, X509Certificate c, WebRequest r, int p){return true;} }
"@ -ErrorAction SilentlyContinue
        [System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAll2 } catch {}
}
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

Write-Log INFO "══════════ SecureGateSync 시작 ══════════"
Write-Log INFO "서버      : $ServerBaseUrl"
Write-Log INFO "토큰      : $(if($Token){$Token.Substring(0,[Math]::Min(8,$Token.Length))+'...'}else{'(미설정!)'})"
Write-Log INFO "받는폴더  : $WorkFolder"
Write-Log INFO "SecureGate: $SecureGateExe"
Write-Log INFO "폴링주기  : ${IntervalSeconds}s / DryRun=$DryRun / $(if($Once){'1회'}else{'상시'})"
Write-Log INFO "※ 목록에 얹기까지만 자동. 전송은 SecureGate 창에서 직접 클릭."
if (-not $Token) { Write-Log ERROR "Token 미설정. 설치를 다시 진행하세요."; return }

if ($Once) { Invoke-SyncOnce; Write-Log INFO "1회 완료(-Once)"; return }
Write-Log INFO "중지: Ctrl+C"
try { while ($true) { Invoke-SyncOnce; Start-Sleep -Seconds $IntervalSeconds } }
finally { Write-Log INFO "══════════ SecureGateSync 종료 ══════════" }
