<#
  SecureGateSync 원클릭 설치 (컴파일 에이전트 방식)
  · 이 파일 하나로: C# 에이전트 로컬 컴파일 → 토큰/서버 설정 → 시작프로그램 자동시작 등록 → 즉시 실행
  · 이후 폰 업로드 → 4초 내 자동 다운로드 → SecureGate 전송 목록에 자동 투입 (보내기만 사람이 클릭)
  · powershell 은 로컬 작업(컴파일/설정/등록)만 하고, 서버 접속은 '컴파일된 exe'가 담당(보안정책 우회).
  · base64/난독화 없음. 관리자 권한 불필요.

  실행: 우클릭 → 속성 → '차단 해제' 체크 → 확인,  그다음 우클릭 → PowerShell에서 실행
        (또는)  powershell -NoProfile -ExecutionPolicy Bypass -File .\SecureGate-Setup.ps1
  제거: powershell -NoProfile -ExecutionPolicy Bypass -File .\SecureGate-Setup.ps1 -Uninstall
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ServerBaseUrl = '__SERVER__'
$Token         = '__TOKEN__'

$InstallDir = Join-Path $env:LOCALAPPDATA 'SecureGateSync'
$exe = Join-Path $InstallDir 'SecureGateSyncAgent.exe'
$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'SecureGateSyncAgent.lnk'

if ($Uninstall) {
    Get-Process SecureGateSyncAgent -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
    Remove-Item -LiteralPath $lnk -Force -EA SilentlyContinue
    Remove-Item -LiteralPath $InstallDir -Recurse -Force -EA SilentlyContinue
    Write-Host '제거 완료(에이전트 종료 + 시작프로그램 삭제 + 폴더 삭제).' -ForegroundColor Green
    return
}

if (($ServerBaseUrl -like '*__SERVER__*') -or -not $Token -or ($Token -like '*__TOKEN__*')) {
    Write-Host '토큰/서버가 설정되지 않았습니다. 관리 콘솔에서 받은 설치파일로 실행하세요.' -ForegroundColor Red
    return
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

# ── C# 에이전트 소스 (서버가 여기에 삽입) ─────────────────────────
$src = @'
__CSHARP__
'@
$csPath = Join-Path $InstallDir 'SecureGateSyncAgent.cs'
[IO.File]::WriteAllText($csPath, $src, (New-Object Text.UTF8Encoding($true)))

# ── 컴파일 (.NET Framework csc, Windows 기본 내장) ────────────────
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { Write-Host '.NET Framework 컴파일러(csc)를 찾을 수 없습니다.' -ForegroundColor Red; return }
$cscArgs = @('/nologo','/target:exe',"/out:$exe",'/r:System.Web.Extensions.dll',$csPath)
& $csc $cscArgs | Out-Null
if (-not (Test-Path $exe)) { Write-Host '에이전트 컴파일 실패.' -ForegroundColor Red; return }

# ── 설정 파일 ────────────────────────────────────────────────────
$work    = Join-Path $InstallDir 'incoming'
$listdir = Join-Path $env:USERPROFILE 'AppData\LocalLow\HANSSAK\RList'
$log     = Join-Path $InstallDir 'agent.log'
$conf = @"
server=$ServerBaseUrl
token=$Token
dest=$work
interval=4000
feed=true
securegate=C:\HANSSAK\SecureGateEX\SecureGate.exe
listdir=$listdir
log=$log
"@
[IO.File]::WriteAllText((Join-Path $InstallDir 'SecureGateSyncAgent.config'), $conf, (New-Object Text.UTF8Encoding($false)))

# ── 자동시작(시작프로그램 바로가기) + 즉시 실행 ──────────────────
try {
    $wsh = New-Object -ComObject WScript.Shell
    $sc = $wsh.CreateShortcut($lnk)
    $sc.TargetPath = $exe
    $sc.WorkingDirectory = $InstallDir
    $sc.WindowStyle = 7
    $sc.Description = 'SecureGateSync 자동 사진 동기화'
    $sc.Save()
} catch { Write-Host "시작프로그램 등록 실패: $($_.Exception.Message)" -ForegroundColor Yellow }

Get-Process SecureGateSyncAgent -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Process -FilePath $exe -WindowStyle Hidden

Write-Host ''
Write-Host '✅ 설치 완료! 로그인할 때마다 자동 시작되며, 지금부터 폰 업로드가' -ForegroundColor Green
Write-Host '   4초 내 자동으로 SecureGate 전송 목록에 얹힙니다. (보내기만 직접 클릭)' -ForegroundColor Green
Write-Host "   설정/로그 폴더: $InstallDir"
Write-Host '   SecureGate.exe 경로가 다르면 위 폴더의 SecureGateSyncAgent.config 의 securegate= 만 수정하세요.'
Write-Host '   이 창은 닫아도 됩니다.'
