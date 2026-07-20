<#
  SecureGate 사진 자동전송 — GUI 앱 설치 (로컬 컴파일)
  · 이 파일 하나로: WinForms GUI 앱을 로컬에서 컴파일 → 바탕화면/시작프로그램 바로가기 생성 → 실행
  · 앱 창에서 사번 입력 → 발급/등록 → QR 확인, 이후 폰 업로드가 자동으로 SecureGate 목록에 얹힘.
  · 서버 접속은 컴파일된 exe가 담당(powershell 차단 우회). base64/난독화 없음. 관리자 권한 불필요.
  실행: 우클릭 → 속성 → 차단 해제 → 확인,  그다음 우클릭 → PowerShell에서 실행
  제거: powershell -NoProfile -ExecutionPolicy Bypass -File .\SecureGate-Setup.ps1 -Uninstall
#>
[CmdletBinding()]
param([switch]$Uninstall)

$Version       = '__VERSION__'   # 설치파일 버전(서버 빌드시각 기준)
$ServerBaseUrl = '__SERVER__'
$Token         = '__TOKEN__'   # 비어있을 수 있음(앱에서 사번 입력)
Write-Host ("SecureGate 자동전송 설치 v{0}" -f $Version) -ForegroundColor Cyan

$InstallDir = Join-Path $env:LOCALAPPDATA 'SecureGateSync'
$exe        = Join-Path $InstallDir 'SecureGateSyncUI.exe'
$desktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'SecureGate 자동전송.lnk'
$startupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'SecureGateSync.lnk'

if ($Uninstall) {
    Get-Process SecureGateSyncUI, SecureGateSyncAgent -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
    Remove-Item -LiteralPath $desktopLnk, $startupLnk -Force -EA SilentlyContinue
    Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('Startup')) 'SecureGateSyncAgent.lnk') -Force -EA SilentlyContinue
    Remove-Item -LiteralPath $InstallDir -Recurse -Force -EA SilentlyContinue
    Write-Host '제거 완료(앱 종료 + 바로가기 삭제 + 폴더 삭제).' -ForegroundColor Green
    return
}
if ($ServerBaseUrl -like '*__SERVER__*') {
    Write-Host '서버가 설정되지 않았습니다. 관리 콘솔/enroll 에서 받은 설치파일로 실행하세요.' -ForegroundColor Red
    return
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
# 기존 헤드리스 에이전트/자동시작 정리
Get-Process SecureGateSyncAgent, SecureGateSyncUI -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('Startup')) 'SecureGateSyncAgent.lnk') -Force -EA SilentlyContinue

# ── C# GUI 소스 (서버가 삽입) ──
$src = @'
__CSHARP__
'@
$csPath = Join-Path $InstallDir 'SecureGateSyncUI.cs'
[IO.File]::WriteAllText($csPath, $src, (New-Object Text.UTF8Encoding($true)))

# ── 컴파일 (WinForms) ──
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { Write-Host '.NET Framework 컴파일러(csc)를 찾을 수 없습니다.' -ForegroundColor Red; return }
$cscArgs = @('/nologo','/target:winexe',"/out:$exe",
             '/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:Microsoft.CSharp.dll',
             $csPath)
& $csc $cscArgs | Out-Null
if (-not (Test-Path $exe)) { Write-Host 'GUI 앱 컴파일 실패.' -ForegroundColor Red; return }

# ── 작업 폴더(비숨김; SecureGate가 AppData를 숨김으로 거부) ──
$dest = 'C:\SecureGateWatch'
try { New-Item -ItemType Directory -Force -Path $dest -ErrorAction Stop | Out-Null }
catch { $dest = Join-Path $env:USERPROFILE 'SecureGateWatch'; New-Item -ItemType Directory -Force -Path $dest | Out-Null }
$listdir = Join-Path $env:USERPROFILE 'AppData\LocalLow\HANSSAK\RList'

$conf = @"
server=$ServerBaseUrl
token=$Token
dest=$dest
securegate=C:\HANSSAK\SecureGateEX\SecureGate.exe
listdir=$listdir
"@
[IO.File]::WriteAllText((Join-Path $InstallDir 'ui.config'), $conf, (New-Object Text.UTF8Encoding($false)))

# ── 바탕화면 + 시작프로그램 바로가기 ──
try {
    $wsh = New-Object -ComObject WScript.Shell
    $d = $wsh.CreateShortcut($desktopLnk); $d.TargetPath = $exe; $d.WorkingDirectory = $InstallDir
    $d.Description = 'SecureGate 사진 자동전송'; $d.Save()
    $s = $wsh.CreateShortcut($startupLnk); $s.TargetPath = $exe; $s.Arguments = '/tray'; $s.WorkingDirectory = $InstallDir
    $s.WindowStyle = 7; $s.Description = 'SecureGate 사진 자동전송'; $s.Save()
} catch { Write-Host "바로가기 생성 실패: $($_.Exception.Message)" -ForegroundColor Yellow }

# ── 앱 실행 ──
Start-Process -FilePath $exe

Write-Host ''
Write-Host '✅ 설치 완료! 바탕화면 [SecureGate 자동전송] 아이콘으로 열 수 있습니다.' -ForegroundColor Green
Write-Host '   앱 창에서 사번 5글자 입력 → [발급/등록] → QR 확인.'
Write-Host '   이후 폰 업로드가 자동으로 SecureGate 전송 목록에 얹힙니다. (보내기만 직접 클릭)'
Write-Host '   설정/로그: ' $InstallDir
Write-Host '   이 창은 닫아도 됩니다.'
