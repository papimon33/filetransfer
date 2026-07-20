# 프로그램 1 — QR 업로드 서버 (폰 → 외부망 PC)

현장에서 폰으로 찍은 업무 사진을, **사용자별 전용 토큰 URL(QR)** 로 업로드하고
외부망 PC에서 같은 URL로 **원본 그대로** 내려받는 웹앱입니다. (FastAPI)

---

## ⚠️ 보안 주의 (먼저 읽어 주세요)

- 업무 사진이 **서버를 경유**합니다. **외부 클라우드(Render)에 배포할 경우 조직
  보안정책 확인이 필요**하며, 가능하면 **사내(외부망) 서버 배포를 권장**합니다.
- 이 서버는 "폰 → 외부망 PC로 사진 옮기기"까지만 담당합니다. 내부망 반입의 실제
  보안 통제(SecureGate 승인·백신검사 등)는 **프로그램 2 이후 단계**에서 그대로
  적용됩니다.

---

## 전체 흐름 속 위치

```
폰(촬영) ─QR─▶ [프로그램1: 이 서버] 업로드
                     │
   외부망 PC에서 같은 토큰 URL 열어 원본 다운로드 → 지정 폴더에 저장
                     │
                     ▼   (이 저장 폴더 = 프로그램2 감시 폴더)
            [프로그램2: 폴더 감시] → SecureGate 전송 대기 목록에 자동 투입
                     │
            사용자가 SecureGate에서 "보내기" 직접 클릭 (자동화 안 함)
```

> **연결 지점:** 외부망 PC에서 **다운로드해 저장하는 폴더**가 곧 프로그램 2의
> **감시 폴더**입니다. (아래 "두 프로그램 연결" 참고)

---

## 화면 구성 (모바일 / PC 자동 분기)

같은 토큰 URL `/u/<토큰>` 을 열면 **접속 기기에 따라 자동으로** 다른 화면이 뜹니다.

| 접속 | 화면 | 내용 |
|------|------|------|
| 📱 폰(모바일) | **업로드 페이지** | "사진 촬영" / "앨범에서 선택" 버튼, 썸네일 미리보기, 큰 업로드 버튼 |
| 💻 PC(데스크톱) | **다운로드 페이지** | 내 파일 목록, 다운로드, 폴더 연결, 모바일 QR 모달 |

- 강제 지정: `/u/<토큰>/m`(모바일 업로드) · `/u/<토큰>/d`(PC 다운로드)
- **SecureGate 폴더 연결(자동 저장):** PC 다운로드 페이지의 **"📁 SecureGate 폴더 연결"** 을
  누르고 감시 폴더(예: `C:\SecureGateWatch`)를 한 번 지정하면, 이후 다운로드가 **그 폴더로
  바로 저장**됩니다(브라우저 File System Access API). → 프로그램 2가 즉시 집어감.
  - 지원: **Chrome/Edge**(크로미움). Firefox/Safari는 미지원 → 브라우저 기본 다운로드 폴더를
    감시 폴더로 설정해 쓰세요.
- **다운로드 = 소비:** 다운로드가 끝난 파일은 **서버에서 자동 삭제**되어 목록에서 사라집니다
  (`DELETE_ON_DOWNLOAD=false` 로 끌 수 있음). 실패 시엔 삭제하지 않아 유실 방지.
- **PC에서 모바일 QR 보기:** 다운로드 페이지의 **"📱 모바일 QR"** 버튼 → 모달로 QR 표시.

## 핵심 기능

- **사용자별 격리**: 각자 `/u/<token>` 전용 URL. 저장도 `uploads/<token>/` 로 물리 분리.
  자기 파일만 보이고 다운로드 가능. 다른 토큰 폴더 접근은 서버가 차단.
- **원본 그대로**: ZIP으로 묶지 않고 **파일 단위 원본** 다운로드. "전체 순차 다운로드"
  버튼으로 여러 장을 차례로 받는 편의 기능 제공(서버 압축 없음).
- **한글 파일명 유지**: `Content-Disposition: filename*` 로 한글 파일명 보존.
- **보안**: 추측 불가 랜덤 토큰, 확장자 화이트리스트(jpg/jpeg/png/heic/webp),
  개당 용량 제한(기본 30MB), Path Traversal 차단.
- **자동 삭제**: 업로드 후 N시간(기본 6h) 경과 파일 백그라운드 자동 삭제.
- **(선택) PIN 게이트**: 환경변수로 켜면 PIN 입력 후 접근(서명된 세션 쿠키 사용,
  localStorage 미사용). 기본은 토큰만.
- **스트리밍 다운로드**: `FileResponse` 로 메모리에 통째로 올리지 않음.

---

## 로컬 실행

### 1) 설치
```bash
cd qr-upload-server
python -m venv .venv
# Windows
.venv\Scripts\activate
# (macOS/Linux) source .venv/bin/activate
pip install -r requirements.txt
```
> **사내 프록시(자체 서명 인증서) 환경에서 pip SSL 오류가 나면:**
> ```bash
> pip install -r requirements.txt --trusted-host pypi.org --trusted-host files.pythonhosted.org
> ```

### 2) 토큰 발급 (관리자)
```bash
python app.py issue --name "홍길동"
# → 전용 URL + ASCII QR 출력, qrcodes/<token>.png 저장
```

### 3) 서버 실행
```bash
python app.py run
# 또는
uvicorn app:app --host 0.0.0.0 --port 8000
```
실행하면 콘솔에 **활성 토큰별 URL과 QR**이 출력됩니다.

> **폰에서 QR을 찍으려면** 폰과 서버 PC가 같은 망(LAN)이어야 하고, QR이 가리키는
> 주소가 접근 가능해야 합니다. `BASE_URL` 미설정 시 서버가 LAN IP를 자동 감지해
> `http://<LAN IP>:8000` 로 QR을 만듭니다. 고정하려면 `BASE_URL` 환경변수를 지정하세요.

---

## QR 보기 (웹에서 바로)

콘솔 ASCII/PNG 파일 말고도 **웹에서 바로** 볼 수 있습니다.

| 경로 | 용도 |
|------|------|
| `/u/<토큰>/qr.png` | QR 이미지 자체(브라우저에서 열면 그림). 저장/캡처해 전달 |
| `/u/<토큰>/qr` | QR + URL을 크게 보여주는 페이지(화면에 띄워 보여주기) |
| `/admin` | **관리 콘솔**(로그인 후) — 토큰 발급/폐기, 전체 QR, 설치파일, 인쇄 |

- `/admin` 접속 → **관리자 비밀번호 로그인**(세션 쿠키). URL에 키를 붙이지 않습니다.
- 비밀번호 = 환경변수 **`ADMIN_PASSWORD`**(권장, 기억하기 쉬운 값) 또는 **`ADMIN_KEY`**.
  둘 다 비우면 `/admin` 비활성화(404).
- 관리자 화면에서 **인쇄** 버튼으로 모든 QR을 종이로 뽑아 현장에 배포할 수 있습니다.
- ⚠️ 모든 토큰/설치파일에 접근되므로 **강한 비밀번호**로 두고 HTTPS로만 접속하세요.
  키가 노출됐다면 `ADMIN_PASSWORD`/`ADMIN_KEY` 를 바꿔 즉시 교체(rotate)하면 됩니다.

## 토큰 발급 — 사번 자가발급 (`/enroll`)

`https://<앱>.onrender.com/enroll` 접속 → **사번 5글자(영문/숫자)** 입력 → 발급:
- **개인 업로드 QR**(폰용) + **PC 설치파일(.ps1)** 다운로드 버튼이 나옵니다.
- **이미 발급된 사번**이면 경고 후 **새 토큰으로 재발급**(기존 토큰·설치는 무효 → 새 설치파일로 재설치).
- 토큰은 **MongoDB Atlas**(`MONGODB_URI` 설정 시)에 저장되어 재배포·재시작에도 유지됩니다.
- (선택) `ENROLL_KEY` 를 설정하면 발급 시 가입코드도 요구합니다.

관리자는 `/admin` 로그인 → 발급 현황(사번·QR·설치파일·발급시각) 조회/폐기.

## 사무실 PC 자동연동 — 원클릭 설치 (권장)

처음 쓰는 사람도 **더블클릭 한 번**으로 끝나도록, 관리 콘솔에서 **개인별 설치파일**을 받습니다.

1. 관리자: `/admin` 로그인 후 그 사람 카드의 **⬇️ PC 설치파일(.ps1)** 다운로드
2. 그 사람: 받은 `SecureGate-Setup.ps1` 을 자기 PC에서 한 번만 실행
   - 우클릭 → **속성** → **차단 해제** 체크(인터넷 파일 표시 제거) → 확인
   - 우클릭 → **PowerShell에서 실행** (또는 `powershell -ExecutionPolicy Bypass -File .\SecureGate-Setup.ps1`)
   - 작은 동기화 프로그램을 **로컬에서 컴파일**(Windows 내장 C# 컴파일러) → **시작프로그램 등록** → 즉시 실행
3. 이후: 폰으로 올린 사진이 **4초 내 자동으로** 다운로드 + SecureGate 전송 목록에 투입
   → 사람은 SecureGate 창에서 **"보내기"만** 클릭

> **왜 컴파일 exe 인가:** 이 환경의 보안 프로그램은 **`powershell.exe`의 외부 접속만 차단**하고
> 일반 exe는 허용합니다(테스트로 확인). 그래서 서버 접속은 **컴파일된 에이전트 exe**가 담당하고,
> PowerShell(설치파일)은 로컬 작업(컴파일/설정/등록)만 합니다. → PowerShell 차단을 우회.
> 에이전트가 pull + SecureGate 투입을 **한 exe로** 수행하므로 별도 프로그램2가 필요 없습니다.
>
> 설정/로그: `%LOCALAPPDATA%\SecureGateSync\`. SecureGate.exe 경로가 다르면 거기의
> `SecureGateSyncAgent.config` 의 `securegate=` 만 수정. 자동시작은 **시작프로그램 폴더 바로가기**
> (작업 스케줄러 API를 막는 환경 대응). base64/난독화 없음, 관리자 권한 불필요.
> 제거: `powershell -ExecutionPolicy Bypass -File .\SecureGate-Setup.ps1 -Uninstall`

## 토큰 관리 (CLI)

| 명령 | 설명 |
|------|------|
| `python app.py issue --name "이름" [--pin 1234]` | 새 토큰 발급 → URL·QR 출력 |
| `python app.py list` | 토큰 목록(활성/폐기, PIN 여부, URL) |
| `python app.py revoke <token>` | 토큰 폐기(즉시 접근 차단) |
| `python app.py qr <token>` | 특정 토큰 URL/QR 다시 출력 |

토큰은 `tokens.json` 에 보관됩니다(폐기해도 기록 유지, `revoked=true` 로 표시).

---

## 사용 흐름

1. **관리자**: `issue` 로 사용자별 토큰 발급 → 각자에게 QR/URL 전달.
2. **현장(폰)**: QR 스캔 → 업로드 페이지에서 사진 여러 장 선택/촬영 → 업로드.
   업로드 후 "N장 업로드 완료" 피드백 표시.
3. **외부망 PC**: 같은 토큰 URL 열기 → "내 파일" 목록에서 각 파일을 원본 다운로드
   (또는 "전체 순차 다운로드"). **지정 폴더에 저장** → 이후 프로그램 2가 처리.

---

## Render 배포 (단계별)

### 0) 먼저 알아둘 점 — 무료 vs 유료
Render 무료 플랜은 **파일시스템이 임시**입니다(재배포·재시작·15분 유휴 후 슬립 시 초기화).
그래서 두 가지를 고려해야 합니다.

- **토큰 유지** → 토큰을 서버 파일이 아니라 **환경변수 `PRESET_TOKENS`** 로 고정합니다(아래 3단계). 무료에서도 재시작 후 토큰이 유지됩니다.
- **업로드 파일 유지** → 무료는 슬립 시 파일이 사라질 수 있습니다. "현장 업로드 → 사무실에서 나중에 다운로드"처럼 **시간 간격이 있으면**, 그 사이 서버가 슬립하면 파일이 날아갈 수 있습니다.
  - **테스트/즉시 다운로드**용이면 무료로 충분.
  - **실사용(간격 있음)** 이면 **유료 Starter($7/월) + 영구 Disk** 를 권장합니다(`render.yaml` 의 `disk:` 주석 해제, `UPLOAD_DIR/TOKENS_FILE/QR_DIR` 를 `/data` 아래로). 슬립도 없고 파일도 보존됩니다.

### 1) 코드를 GitHub에 올리기
이 리포(또는 `qr-upload-server` 폴더 포함 리포)를 GitHub에 push 합니다.

### 2) Render에서 웹 서비스 생성
[dashboard.render.com](https://dashboard.render.com) → **New +** →
- **Blueprint** 선택 후 이 리포를 고르면 `render.yaml` 대로 자동 구성됩니다. (권장)
- 또는 **Web Service** 를 수동 생성:
  - **Root Directory**: `qr-upload-server`
  - **Build Command**: `pip install -r requirements.txt`
  - **Start Command**: `uvicorn app:app --host 0.0.0.0 --port $PORT`

### 3) 토큰을 로컬에서 만들어 환경변수에 넣기
로컬에서 사용자별 토큰을 발급합니다(서버가 아니라 내 PC에서):
```bash
python app.py issue --name "홍길동"    # 출력된 '토큰' 문자열을 복사
python app.py issue --name "김철수"
```
Render 대시보드 → 서비스 → **Environment** 에 아래를 등록:
| Key | Value(예시) |
|-----|-------------|
| `PRESET_TOKENS` | `AbC..홍길동토큰=홍길동,XyZ..김철수토큰=김철수` |
| `BASE_URL` | (4단계에서 나온 도메인) `https://qr-upload-server.onrender.com` |

> `PRESET_TOKENS` 형식은 `토큰=이름` 을 쉼표로 이어 붙입니다. 이렇게 하면 무료 플랜에서
> 재배포·슬립 후에도 **같은 URL/QR** 이 계속 유효합니다.

### 4) 배포 & BASE_URL 채우기
첫 배포가 끝나면 `https://<앱이름>.onrender.com` 도메인이 생깁니다.
이 값을 `BASE_URL` 에 넣고 저장하면 재배포됩니다. (HTTPS는 Render가 자동 처리)

### 5) QR 확인 → 배포
- Render **Logs** 탭에 시작 시 각 토큰의 URL/QR(ASCII)이 찍힙니다.
- 또는 로컬에서 `BASE_URL=https://<앱>.onrender.com python app.py qr <토큰>` 으로 QR PNG를 뽑아
  현장 작업자에게 전달합니다.
- 현장 폰으로 QR 스캔 → 업로드 → 사무실 외부망 PC에서 같은 URL 열어 다운로드.

> **정리:** 무료로 시작해도 되지만, "현장에서 올리고 사무실에서 나중에 받는" 실제 운영이라면
> 파일 보존을 위해 **유료 + Disk** 를 권장합니다. 토큰은 `PRESET_TOKENS` 로 무료에서도 유지됩니다.

---

## 환경변수

| 변수 | 기본값 | 설명 |
|------|--------|------|
| `BASE_URL` | (LAN IP 자동) | QR/URL이 가리킬 공개 주소. Render면 `https://...onrender.com` |
| `HOST` | `0.0.0.0` | 바인딩 호스트 |
| `PORT` | `8000` | 포트 (Render는 자동 주입) |
| `UPLOAD_DIR` | `./uploads` | 업로드 저장 루트 |
| `TOKENS_FILE` | `./tokens.json` | 토큰 저장소 |
| `QR_DIR` | `./qrcodes` | QR PNG 저장 폴더 |
| `ALLOWED_EXT` | `jpg,jpeg,png,heic,webp` | 허용 확장자 |
| `MAX_FILE_MB` | `30` | 개당 최대 업로드 크기(MB) |
| `RETENTION_HOURS` | `6` | 이 시간 지난 파일 자동 삭제 |
| `CLEANUP_INTERVAL_MIN` | `30` | 자동 삭제 점검 주기(분) |
| `MONGODB_URI` | (없음) | MongoDB Atlas 연결문자열. 설정 시 토큰을 DB에 영구 저장(재배포 유지) |
| `MONGODB_DB` | `filetransfer` | MongoDB 데이터베이스 이름 |
| `ENROLL_KEY` | (없음) | `/enroll` 사번 발급 가입코드(선택) |
| `ADMIN_PASSWORD` | (없음) | 관리 콘솔 로그인 비밀번호(권장). 미설정 시 `ADMIN_KEY` 사용 |
| `ADMIN_KEY` | (없음) | 관리 콘솔 접근/로그인 대체값. 둘 다 비면 `/admin` 비활성화 |
| `REQUIRE_PIN` | `false` | PIN 게이트 on/off |
| `SESSION_SECRET` | (랜덤) | 세션 쿠키 서명 키. **관리자 로그인 유지**하려면 고정값 지정(Render는 자동생성) |
| `PRINT_QR_ON_START` | `true` | 시작 시 콘솔 ASCII QR 출력 |
| `PRESET_TOKENS` | (없음) | 고정 토큰 `토큰=이름,토큰=이름`. Render 무료 플랜에서 토큰 유지용 |
| `DELETE_ON_DOWNLOAD` | `true` | 다운로드한 파일을 서버에서 자동 삭제(소비형) |

`.env.example` 참고. (로컬은 `python-dotenv` 미사용 → 셸에서 직접 export 하거나 실행 앞에 지정)

---

## 두 프로그램 연결 (프로그램 2와 폴더 공유)

외부망 PC에서 **내 토큰 파일을 다운로드해 저장하는 폴더**를 프로그램 2의 감시 폴더로
맞춥니다. 예를 들어 `C:\SecureGateWatch` 에 저장한다면 프로그램 2 설정을 이렇게:

`../SecureGateAutoList.config.psd1`
```powershell
WatchFolder = 'C:\SecureGateWatch'   # ← 여기에 QR서버에서 받은 사진을 저장
```

- 사용자별로 파일이 격리되므로 **내 파일만 내 자동전송에 흘러가고** 다른 사람 것과 안 섞입니다.
- **직원마다 각자 PC에서** 쓰려면, 각자 자기 토큰 URL에서 받은 파일을 각자의 감시 폴더에
  저장하고, 프로그램 2의 `WatchFolder` 만 그 폴더로 바꾸면 됩니다.
- 실제 SecureGate에 붙이기 전 프로그램 2를 **`-DryRun`** 으로 먼저 돌려보길 권합니다.

전체 시스템 개요는 상위 폴더의 **`../README-SYSTEM.md`** 참고.

---

## 보안 세부

- **토큰**: `secrets.token_urlsafe(24)` (추측 불가). 토큰 형식(영숫자/`-`/`_`)이 아니면 즉시 거부.
- **격리/차단**: 다운로드 시 요청 경로를 해당 토큰 폴더 기준으로 resolve 후 **폴더 밖이면 404**.
  파일명은 basename만 취하고 제어문자 제거(유니코드/한글은 보존).
- **확장자/용량**: 화이트리스트 + 청크 스트리밍으로 크기 초과 시 중단·삭제.
- **토큰 노출 방지**: 루트 `/` 는 토큰 목록을 노출하지 않음. 없는/폐기된 토큰은 404.
- **PIN(선택)**: 통과 상태는 서명된 세션 쿠키에만 저장(브라우저 localStorage 미사용).

## 검증(개발용)

```bash
pip install httpx   # (테스트에만 필요, 사내 프록시면 --trusted-host 추가)
python _test_app.py
```
격리·traversal·확장자·용량·한글파일명·PIN 등 24개 항목을 in-process로 검증합니다.
