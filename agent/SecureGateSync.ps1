<#
.SYNOPSIS
  SecureGateSync — QR 업로드 서버에서 '내 토큰' 사진을 자동으로 당겨와 SecureGate
  전송 대기 목록에 자동으로 얹는 통합 스크립트. (설치 + 실행 겸용)

.DESCRIPTION
  · 인자 없이 실행 → '설치': 자기 자신을 LOCALAPPDATA로 복사하고 '시작프로그램 폴더'에
    로그온 자동시작 바로가기를 등록한 뒤 즉시 시작한다. (작업 스케줄러 API 미사용 → 보안정책에 덜 막힘)
  · -Run          → '실행'(자동시작이 이 인자로 호출): 3초마다 서버에서 새 사진을
    받아 SecureGate 목록에 투입한다.
  · -Uninstall    → 등록 해제(시작프로그램 바로가기 삭제 + 실행 중이면 종료).

  ★ '목록에 얹기'까지만 자동. 실제 전송(보내기)은 사용자가 SecureGate 창에서 직접 클릭.
  ※ base64/난독화 없이 읽을 수 있는 일반 스크립트다(백신 오탐 최소화).

.NOTES
  Windows PowerShell 5.1. 실행:
    powershell -NoProfile -ExecutionPolicy Bypass -File .\SecureGate-Setup-<이름>.ps1
#>

[CmdletBinding()]
param([switch]$Run, [switch]$Uninstall)

# ===== 설정 (관리 콘솔이 설치파일 생성 시 아래 두 값을 채움) =====
$ServerBaseUrl = '__SERVER__'
$Token         = '__TOKEN__'

# ===== 기본값 =====
$TaskName         = 'SecureGateSync'
$InstallDir       = Join-Path $env:LOCALAPPDATA 'SecureGateSync'
$IntervalSeconds  = 3
$SecureGateExe    = 'C:\HANSSAK\SecureGateEX\SecureGate.exe'
$WorkFolder       = Join-Path $InstallDir 'incoming'
$ListOutputFolder = Join-Path $env:USERPROFILE 'AppData\LocalLow\HANSSAK\RList'
$LogFile          = Join-Path $InstallDir 'sync.log'

# 설치 후에는 config 파일에서 값을 읽는다(placeholder가 안 채워진 채 직접 실행하는 경우도 대비).
$cfgPath = Join-Path $InstallDir 'SecureGateSync.config.psd1'
if (($ServerBaseUrl -like '*__SERVER__*') -or (-not $ServerBaseUrl)) {
    if (Test-Path -LiteralPath $cfgPath) {
        try { $c = Import-PowerShellDataFile -Path $cfgPath
              $ServerBaseUrl = "$($c.ServerBaseUrl)"; $Token = "$($c.Token)"
              if ($c.IntervalSeconds) { $IntervalSeconds = [int]$c.IntervalSeconds } } catch {}
    }
}
$ServerBaseUrl = ("$ServerBaseUrl").TrimEnd('/')
if ($IntervalSeconds -lt 1) { $IntervalSeconds = 1 }

# ===== 공통 함수 =====
function Write-Log { param([ValidateSet('INFO','WARN','ERROR')][string]$Level='INFO',[string]$Message)
    $ts=(Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); $line="[$ts] [$Level] $Message"
    switch($Level){'ERROR'{Write-Host $line -ForegroundColor Red}'WARN'{Write-Host $line -ForegroundColor Yellow}default{Write-Host $line}}
    try{ $d=Split-Path -Parent $LogFile; if($d -and -not(Test-Path -LiteralPath $d)){New-Item -ItemType Directory -Path $d -Force|Out-Null}
         Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8 }catch{}
}

# ===== 제거 =====
if ($Uninstall) {
    $lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'SecureGateSync.lnk'
    Remove-Item -LiteralPath $lnk -Force -EA SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -EA SilentlyContinue   # 옛 작업분도 정리
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -EA SilentlyContinue |
        Where-Object { $_.CommandLine -like '*SecureGateSync.ps1*-Run*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
    Write-Host '제거 완료(시작프로그램/작업 삭제, 실행 중이면 종료).' -ForegroundColor Green
    return
}

# ===== 설치 (인자 없이 실행) =====
if (-not $Run) {
    if (($ServerBaseUrl -like '*__SERVER__*') -or (-not $Token) -or ($Token -like '*__TOKEN__*')) {
        Write-Host '토큰/서버가 설정되지 않았습니다. 관리 콘솔에서 받은 설치파일로 실행하세요.' -ForegroundColor Red
        return
    }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    $dest = Join-Path $InstallDir 'SecureGateSync.ps1'
    Copy-Item -LiteralPath $PSCommandPath -Destination $dest -Force
    $cfg = "@{`r`n  ServerBaseUrl = '$ServerBaseUrl'`r`n  Token = '$Token'`r`n  IntervalSeconds = $IntervalSeconds`r`n}"
    [IO.File]::WriteAllText($cfgPath, $cfg, (New-Object Text.UTF8Encoding($true)))

    # 로그인 자동시작: '시작프로그램 폴더' 바로가기 방식(작업 스케줄러 API 불필요 → 보안정책에 덜 막힘).
    $psExe   = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $runArgs = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $dest + '" -Run'
    $ok = $false
    try {
        $lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'SecureGateSync.lnk'
        $wsh = New-Object -ComObject WScript.Shell
        $sc  = $wsh.CreateShortcut($lnk)
        $sc.TargetPath       = $psExe
        $sc.Arguments        = $runArgs
        $sc.WorkingDirectory = $InstallDir
        $sc.WindowStyle      = 7
        $sc.Description       = 'SecureGateSync 자동 동기화'
        $sc.Save()
        $ok = $true
    } catch {
        Write-Host "시작프로그램 등록 실패: $($_.Exception.Message)" -ForegroundColor Red
    }
    # 지금 즉시 백그라운드로 한 번 시작
    try { Start-Process -FilePath $psExe -ArgumentList $runArgs -WindowStyle Hidden } catch {}

    Write-Host ''
    if ($ok) {
        Write-Host '설치 완료! 로그인할 때마다 자동 시작되며, 지금부터 동작합니다.' -ForegroundColor Green
    } else {
        Write-Host '자동시작 등록은 실패했지만 지금은 실행됩니다. (보안정책이면 IT에 허용 요청)' -ForegroundColor Yellow
    }
    Write-Host "설정/로그 폴더: $InstallDir"
    Write-Host '이 창은 닫아도 됩니다.'
    return
}

# ===== 실행(-Run): 동기화 루프 =====
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

function Get-UniqueDest { param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $Path }
    $dir=Split-Path -Parent $Path; $b=[IO.Path]::GetFileNameWithoutExtension($Path); $e=[IO.Path]::GetExtension($Path); $i=1
    do { $c=Join-Path $dir ("{0}({1}){2}" -f $b,$i,$e); $i++ } while (Test-Path -LiteralPath $c); return $c
}
function New-ListFile { param([string[]]$Paths)
    if (-not (Test-Path -LiteralPath $ListOutputFolder)) { New-Item -ItemType Directory -Path $ListOutputFolder -Force | Out-Null }
    $stamp=(Get-Date).ToString('yyyyMMddHHmmss'); $lp=Join-Path $ListOutputFolder ($stamp+'.txt'); $i=1
    while (Test-Path -LiteralPath $lp) { $lp=Join-Path $ListOutputFolder ("{0}_{1}.txt" -f $stamp,$i); $i++ }
    [IO.File]::WriteAllText($lp, (($Paths -join "`r`n")+"`r`n"), [Text.Encoding]::Unicode)   # UTF-16LE+BOM
    return $lp
}
function Invoke-SecureGate { param([string[]]$Paths)
    $lp=New-ListFile -Paths $Paths
    if (-not (Test-Path -LiteralPath $SecureGateExe)) { Write-Log ERROR "SecureGate 없음: $SecureGateExe (목록: $lp)"; return }
    try { Start-Process -FilePath $SecureGateExe -ArgumentList ('F {0} {1}' -f $Paths.Count, $lp) -EA Stop | Out-Null
          Write-Log INFO "SecureGate 투입: F $($Paths.Count) $lp" }
    catch { Write-Log ERROR "SecureGate 실행 실패: $($_.Exception.Message)" }
}
function Invoke-SyncOnce {
    try {
        $raw=Invoke-WebRequest -Uri "$ServerBaseUrl/u/$Token/list" -Method Get -TimeoutSec 30 -UseBasicParsing
        $resp=([Text.Encoding]::UTF8.GetString($raw.RawContentStream.ToArray())) | ConvertFrom-Json
    } catch { Write-Log WARN "목록 조회 실패: $($_.Exception.Message)"; return }
    $files=@($resp.files); if ($files.Count -eq 0) { return }
    Write-Log INFO "새 파일 $($files.Count)개 → 다운로드"
    if (-not (Test-Path -LiteralPath $WorkFolder)) { New-Item -ItemType Directory -Path $WorkFolder -Force | Out-Null }
    $got=@()
    foreach ($f in $files) {
        $name="$($f.name)"; if (-not $name) { continue }
        $url="$ServerBaseUrl/u/$Token/file/$([uri]::EscapeDataString($name))"
        $final=Get-UniqueDest (Join-Path $WorkFolder $name); $part="$final.part"
        try {
            Invoke-WebRequest -Uri $url -OutFile $part -Method Get -TimeoutSec 120 -UseBasicParsing
            Rename-Item -LiteralPath $part -NewName ([IO.Path]::GetFileName($final)) -EA Stop
            $got += $final
            try { Invoke-RestMethod -Uri $url -Method Delete -TimeoutSec 30 | Out-Null } catch { Write-Log WARN "서버삭제 실패: $name" }
        } catch { Write-Log ERROR "다운로드 실패: $name ($($_.Exception.Message))"
                  if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force -EA SilentlyContinue } }
    }
    if ($got.Count -gt 0) { Invoke-SecureGate -Paths $got }
}

Write-Log INFO "SecureGateSync 실행 시작 (서버 $ServerBaseUrl / ${IntervalSeconds}s)"
if (-not $Token -or ($Token -like '*__TOKEN__*')) { Write-Log ERROR "토큰 미설정"; return }
try { while ($true) { Invoke-SyncOnce; Start-Sleep -Seconds $IntervalSeconds } }
finally { Write-Log INFO "SecureGateSync 종료" }
