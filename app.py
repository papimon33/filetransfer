#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
프로그램 1 — QR 업로드 서버 (폰 → 외부망 PC)

현장에서 폰으로 찍은 사진을, 사용자별 전용 토큰 URL(QR)로 업로드하고
외부망 PC에서 같은 토큰 URL로 원본 그대로 내려받는 웹앱.

  폰(QR 스캔) → 업로드 → 외부망 PC에서 다운로드(지정 폴더에 저장)
  → 그 폴더를 [프로그램 2: 폴더 감시]가 감시하여 SecureGate 목록에 자동 투입

핵심 특징
  - 사용자별 격리: /u/<token> , 저장은 uploads/<token>/ 로 물리 분리
  - 원본 파일명(한글 포함) 유지, ZIP 없이 파일 단위 원본 다운로드
  - 경로 조작(Path Traversal) 차단, 확장자/용량 제한
  - 업로드 N시간 후 자동 삭제(백그라운드)
  - (선택) 개인 PIN 게이트(환경변수로 on/off, 기본 off) — 서명된 세션 쿠키 사용

실행
  서버:   uvicorn app:app --host 0.0.0.0 --port 8000
          (또는)  python app.py run
  토큰:   python app.py issue --name "홍길동"
          python app.py list
          python app.py revoke <token>
          python app.py qr <token>
"""

import os
import io
import sys
import json
import time
import base64
import hashlib
import secrets
import asyncio
import mimetypes
from pathlib import Path
from datetime import datetime, timezone
from contextlib import asynccontextmanager

# Windows 콘솔에서 한글/QR 출력이 깨지지 않도록 UTF-8로 재설정
try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

import qrcode
from fastapi import FastAPI, UploadFile, File, Request, Form, HTTPException
from fastapi.responses import (
    HTMLResponse, FileResponse, JSONResponse, PlainTextResponse, RedirectResponse, Response,
)
from starlette.middleware.sessions import SessionMiddleware


# ──────────────────────────────────────────────────────────────
# 설정 (환경변수)
# ──────────────────────────────────────────────────────────────
def _env(name, default=None):
    v = os.getenv(name)
    return v if v not in (None, "") else default

BASE_DIR        = Path(__file__).resolve().parent

def _safe_dir(configured: str, fallback: Path) -> Path:
    """설정 경로에 폴더를 만들어 보고, 권한/부재 등으로 실패하면 fallback 으로 대체.
    (예: Render 무료 플랜에서 디스크 없이 /data 경로를 지정하면 부팅 크래시 → 방어)."""
    p = Path(configured).resolve()
    try:
        p.mkdir(parents=True, exist_ok=True)
        return p
    except Exception as e:
        fb = fallback.resolve()
        fb.mkdir(parents=True, exist_ok=True)
        print(f"[warn] 경로 '{p}' 사용 불가({e.__class__.__name__}) → '{fb}' 로 대체")
        return fb

UPLOAD_DIR      = _safe_dir(_env("UPLOAD_DIR", str(BASE_DIR / "uploads")), BASE_DIR / "uploads")
QR_DIR          = _safe_dir(_env("QR_DIR", str(BASE_DIR / "qrcodes")), BASE_DIR / "qrcodes")
# TOKENS_FILE 은 파일 → 부모 폴더가 쓰기 가능한지로 판단
_tok_cfg        = Path(_env("TOKENS_FILE", str(BASE_DIR / "tokens.json"))).resolve()
try:
    _tok_cfg.parent.mkdir(parents=True, exist_ok=True)
    TOKENS_FILE = _tok_cfg
except Exception as e:
    (BASE_DIR).mkdir(parents=True, exist_ok=True)
    TOKENS_FILE = (BASE_DIR / "tokens.json").resolve()
    print(f"[warn] TOKENS_FILE '{_tok_cfg}' 사용 불가({e.__class__.__name__}) → '{TOKENS_FILE}' 로 대체")

PORT            = int(_env("PORT", "8000"))
HOST            = _env("HOST", "0.0.0.0")
BASE_URL_ENV    = (_env("BASE_URL", "") or "").rstrip("/")

MAX_FILE_MB     = float(_env("MAX_FILE_MB", "30"))
MAX_FILE_BYTES  = int(MAX_FILE_MB * 1024 * 1024)
RETENTION_HOURS = float(_env("RETENTION_HOURS", "6"))
CLEANUP_MIN     = float(_env("CLEANUP_INTERVAL_MIN", "30"))

ALLOWED_EXT     = set(
    e.strip().lower().lstrip(".")
    for e in _env("ALLOWED_EXT", "jpg,jpeg,png,heic,webp").split(",")
    if e.strip()
)

REQUIRE_PIN     = _env("REQUIRE_PIN", "false").lower() in ("1", "true", "yes", "on")
ADMIN_KEY       = _env("ADMIN_KEY", "")        # 관리 콘솔 접근용(로그인 비밀번호로도 사용됨)
ADMIN_PASSWORD  = _env("ADMIN_PASSWORD", "")   # 관리자 로그인 비밀번호(미설정 시 ADMIN_KEY 사용)
DELETE_ON_DOWNLOAD = _env("DELETE_ON_DOWNLOAD", "true").lower() in ("1", "true", "yes", "on")
SESSION_SECRET  = _env("SESSION_SECRET") or secrets.token_hex(32)
PRINT_QR_ON_START = _env("PRINT_QR_ON_START", "true").lower() in ("1", "true", "yes", "on")


# ──────────────────────────────────────────────────────────────
# 토큰 저장소 (tokens.json)
#   { "<token>": {"name": str, "created": iso, "revoked": bool, "pin": sha256|null} }
# ──────────────────────────────────────────────────────────────
def load_tokens() -> dict:
    if TOKENS_FILE.exists():
        try:
            return json.loads(TOKENS_FILE.read_text(encoding="utf-8"))
        except Exception:
            return {}
    return {}

def save_tokens(data: dict) -> None:
    tmp = TOKENS_FILE.with_suffix(".tmp")
    tmp.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    tmp.replace(TOKENS_FILE)

def hash_pin(token: str, pin: str) -> str:
    return hashlib.sha256(f"{token}:{pin}".encode("utf-8")).hexdigest()

def issue_token(name: str, pin: str | None = None) -> str:
    data = load_tokens()
    token = secrets.token_urlsafe(24)          # 추측 불가능한 랜덤 (URL-safe: [A-Za-z0-9_-])
    data[token] = {
        "name": name,
        "created": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "revoked": False,
        "pin": hash_pin(token, pin) if pin else None,
    }
    save_tokens(data)
    (UPLOAD_DIR / token).mkdir(parents=True, exist_ok=True)
    return token

def revoke_token(token: str) -> bool:
    data = load_tokens()
    if token in data:
        data[token]["revoked"] = True
        save_tokens(data)
        return True
    return False

def get_token_info(token: str) -> dict | None:
    """유효(존재 + 미폐기)한 토큰이면 info dict, 아니면 None."""
    if not is_token_shape(token):
        return None
    info = load_tokens().get(token)
    if not info or info.get("revoked"):
        return None
    return info

def ensure_presets() -> int:
    """
    PRESET_TOKENS 환경변수의 토큰을 저장소에 병합한다.
    형식: "토큰1=이름1,토큰2=이름2" (줄바꿈도 구분자로 허용)
    Render 무료 플랜처럼 파일시스템이 임시라 tokens.json 이 초기화되는 환경에서,
    토큰을 환경변수로 '고정'해 재시작 후에도 동일 토큰이 살아있게 한다.
    """
    raw = _env("PRESET_TOKENS", "")
    if not raw:
        return 0
    data = load_tokens()
    added = 0
    for part in raw.replace("\n", ",").split(","):
        part = part.strip()
        if not part or "=" not in part:
            continue
        tok, name = part.split("=", 1)
        tok, name = tok.strip(), name.strip()
        if not tok or not is_token_shape(tok):
            continue
        (UPLOAD_DIR / tok).mkdir(parents=True, exist_ok=True)
        if tok not in data:
            data[tok] = {
                "name": name or "(preset)",
                "created": datetime.now(timezone.utc).isoformat(timespec="seconds"),
                "revoked": False,
                "pin": None,
                "preset": True,
            }
            added += 1
    if added:
        try:
            save_tokens(data)
        except Exception as e:
            print(f"[preset] 저장 실패(무시): {e}")
    return added


# ──────────────────────────────────────────────────────────────
# 경로/파일 안전 처리
# ──────────────────────────────────────────────────────────────
def is_token_shape(token: str) -> bool:
    # 랜덤 토큰 형식만 허용 (경로 조작 문자 원천 차단)
    return bool(token) and all(c.isalnum() or c in "-_" for c in token) and len(token) <= 64

def user_dir(token: str) -> Path:
    d = (UPLOAD_DIR / token)
    d.mkdir(parents=True, exist_ok=True)
    return d.resolve()

def sanitize_filename(name: str) -> str:
    # 경로 요소 제거 (한글 등 유니코드 파일명은 보존)
    name = (name or "").replace("\\", "/")
    name = name.split("/")[-1]
    name = "".join(ch for ch in name if ord(ch) >= 32)   # 제어문자 제거
    name = name.replace("\x00", "").strip().strip(".").strip()
    if not name:
        name = "file"
    return name[:200]

def unique_name(dirpath: Path, filename: str) -> str:
    base, ext = os.path.splitext(filename)
    candidate = filename
    i = 1
    while (dirpath / candidate).exists():
        candidate = f"{base}({i}){ext}"
        i += 1
    return candidate

def ext_ok(filename: str) -> bool:
    ext = os.path.splitext(filename)[1].lower().lstrip(".")
    return ext in ALLOWED_EXT

def resolve_in_userdir(token: str, filename: str) -> Path | None:
    """token 폴더 '내부'의 실제 파일만 반환. 벗어나면 None (traversal 차단)."""
    udir = user_dir(token)
    safe = sanitize_filename(filename)
    target = (udir / safe).resolve()
    try:
        target.relative_to(udir)          # udir 밖이면 ValueError
    except ValueError:
        return None
    if not target.is_file():
        return None
    return target

def list_user_files(token: str):
    udir = user_dir(token)
    items = []
    for p in udir.iterdir():
        if p.is_file() and not p.name.endswith(".tmp"):
            st = p.stat()
            items.append({
                "name": p.name,
                "size": st.st_size,
                "mtime": st.st_mtime,
            })
    items.sort(key=lambda x: x["mtime"], reverse=True)
    return items


# ──────────────────────────────────────────────────────────────
# QR / URL
# ──────────────────────────────────────────────────────────────
def guess_lan_ip() -> str:
    import socket
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("8.8.8.8", 80))
        return s.getsockname()[0]
    except Exception:
        return "127.0.0.1"
    finally:
        s.close()

def base_url() -> str:
    if BASE_URL_ENV:
        return BASE_URL_ENV
    return f"http://{guess_lan_ip()}:{PORT}"

def upload_url(token: str) -> str:
    return f"{base_url()}/u/{token}"

def qr_ascii(data: str) -> str:
    qr = qrcode.QRCode(border=1)
    qr.add_data(data)
    qr.make(fit=True)
    buf = io.StringIO()
    qr.print_ascii(out=buf, invert=True)
    return buf.getvalue()

def qr_png_bytes(data: str) -> bytes:
    img = qrcode.make(data)
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()

def save_qr_png(token: str) -> Path:
    path = QR_DIR / f"{token}.png"
    path.write_bytes(qr_png_bytes(upload_url(token)))
    return path


# ──────────────────────────────────────────────────────────────
# 백그라운드 자동 삭제
# ──────────────────────────────────────────────────────────────
async def cleanup_loop():
    while True:
        try:
            cutoff = time.time() - RETENTION_HOURS * 3600
            removed = 0
            for udir in UPLOAD_DIR.iterdir():
                if not udir.is_dir():
                    continue
                for f in udir.iterdir():
                    try:
                        if f.is_file() and f.stat().st_mtime < cutoff:
                            f.unlink()
                            removed += 1
                    except Exception:
                        pass
            if removed:
                print(f"[cleanup] {removed}개 파일 자동 삭제 (보관 {RETENTION_HOURS}h 초과)")
        except Exception as e:
            print(f"[cleanup] 오류: {e}")
        await asyncio.sleep(max(60, CLEANUP_MIN * 60))


def print_startup_info():
    tokens = load_tokens()
    active = {t: i for t, i in tokens.items() if not i.get("revoked")}
    print("=" * 70)
    print(" QR 업로드 서버 시작")
    print(f"  기준 URL(BASE_URL) : {base_url()}"
          + ("" if BASE_URL_ENV else "  (BASE_URL 미설정 → LAN IP 자동감지. 폰이 같은 망이어야 함)"))
    print(f"  업로드 폴더        : {UPLOAD_DIR}")
    print(f"  허용 확장자        : {', '.join(sorted(ALLOWED_EXT))} / 개당 최대 {MAX_FILE_MB:.0f}MB")
    print(f"  자동 삭제          : {RETENTION_HOURS:.0f}시간 경과 파일 (점검 {CLEANUP_MIN:.0f}분 간격)")
    print(f"  PIN 게이트         : {'ON' if REQUIRE_PIN else 'OFF'}")
    print(f"  활성 토큰 수       : {len(active)}")
    print("=" * 70)
    if not active:
        print(" 발급된 토큰이 없습니다.  python app.py issue --name \"이름\"  으로 발급하세요.")
        print("=" * 70)
        return
    for token, info in active.items():
        url = upload_url(token)
        try:
            save_qr_png(token)
        except Exception:
            pass
        print(f"\n● {info.get('name','(이름없음)')}")
        print(f"  URL : {url}")
        print(f"  QR  : {QR_DIR / (token + '.png')}")
        if PRINT_QR_ON_START:
            print(qr_ascii(url))
    print("=" * 70)


# ──────────────────────────────────────────────────────────────
# FastAPI 앱
# ──────────────────────────────────────────────────────────────
@asynccontextmanager
async def lifespan(app: FastAPI):
    n = ensure_presets()
    if n:
        print(f"[preset] 환경변수 PRESET_TOKENS 에서 토큰 {n}개 로드")
    print_startup_info()
    task = asyncio.create_task(cleanup_loop())
    try:
        yield
    finally:
        task.cancel()

app = FastAPI(title="QR Upload Server", lifespan=lifespan)
# 세션 쿠키(서명됨). PIN 통과 상태 저장용. localStorage 미사용.
app.add_middleware(SessionMiddleware, secret_key=SESSION_SECRET, same_site="lax", https_only=False)


def require_token(token: str) -> dict:
    info = get_token_info(token)
    if info is None:
        # 토큰 존재 여부를 드러내지 않도록 404
        raise HTTPException(status_code=404, detail="Not found")
    return info

def pin_ok(token: str, info: dict, request: Request) -> bool:
    if not REQUIRE_PIN:
        return True
    if not info.get("pin"):
        return True
    return request.session.get(f"auth:{token}") is True


# ── 라우트 ────────────────────────────────────────────────────
@app.get("/", response_class=PlainTextResponse)
def root():
    # 보안상 토큰 목록을 노출하지 않는다.
    return "QR Upload Server. 개인 토큰 URL(/u/<token>)로 접속하세요."

@app.get("/healthz", response_class=PlainTextResponse)
def healthz():
    return "ok"

def is_mobile(request: Request) -> bool:
    ua = request.headers.get("user-agent", "").lower()
    return any(k in ua for k in ("iphone", "android", "ipad", "ipod", "mobile", "windows phone"))

@app.get("/u/{token}", response_class=HTMLResponse)
def page(token: str, request: Request):
    """QR(폰) → 모바일 업로드 페이지 / PC → 다운로드 페이지 (User-Agent 자동 분기)."""
    info = require_token(token)
    if not pin_ok(token, info, request):
        return HTMLResponse(render_pin_page(token))
    if is_mobile(request):
        return HTMLResponse(render_mobile_upload_page(token, info))
    return HTMLResponse(render_desktop_download_page(token, info))

@app.get("/u/{token}/m", response_class=HTMLResponse)
def page_mobile(token: str, request: Request):
    """모바일 업로드 페이지(수동 강제)."""
    info = require_token(token)
    if not pin_ok(token, info, request):
        return HTMLResponse(render_pin_page(token))
    return HTMLResponse(render_mobile_upload_page(token, info))

@app.get("/u/{token}/d", response_class=HTMLResponse)
def page_desktop(token: str, request: Request):
    """PC 다운로드 페이지(수동 강제)."""
    info = require_token(token)
    if not pin_ok(token, info, request):
        return HTMLResponse(render_pin_page(token))
    return HTMLResponse(render_desktop_download_page(token, info))

@app.post("/u/{token}/pin", response_class=HTMLResponse)
def submit_pin(token: str, request: Request, pin: str = Form(...)):
    info = require_token(token)
    if info.get("pin") and hash_pin(token, pin) == info["pin"]:
        request.session[f"auth:{token}"] = True
        return RedirectResponse(url=f"/u/{token}", status_code=303)
    return HTMLResponse(render_pin_page(token, error="PIN이 올바르지 않습니다."), status_code=401)

@app.post("/u/{token}/upload")
async def upload(token: str, request: Request, files: list[UploadFile] = File(...)):
    info = require_token(token)
    if not pin_ok(token, info, request):
        raise HTTPException(status_code=401, detail="PIN 필요")

    udir = user_dir(token)
    saved, errors = [], []
    for uf in files:
        orig = sanitize_filename(uf.filename or "file")
        if not ext_ok(orig):
            errors.append(f"{orig}: 허용되지 않은 확장자")
            continue
        name = unique_name(udir, orig)
        dest = udir / name
        size = 0
        too_big = False
        try:
            with open(dest, "wb") as out:
                while True:
                    chunk = await uf.read(1024 * 1024)
                    if not chunk:
                        break
                    size += len(chunk)
                    if size > MAX_FILE_BYTES:
                        too_big = True
                        break
                    out.write(chunk)
            if too_big:
                dest.unlink(missing_ok=True)
                errors.append(f"{orig}: 용량 초과(> {MAX_FILE_MB:.0f}MB)")
                continue
            saved.append(name)
        except Exception as e:
            dest.unlink(missing_ok=True)
            errors.append(f"{orig}: 저장 실패({e})")
        finally:
            await uf.close()

    return JSONResponse({"uploaded": len(saved), "saved": saved, "errors": errors})

@app.get("/u/{token}/list")
def api_list(token: str, request: Request):
    info = require_token(token)
    if not pin_ok(token, info, request):
        raise HTTPException(status_code=401, detail="PIN 필요")
    return {"files": list_user_files(token)}

@app.get("/u/{token}/file/{filename}")
def download(token: str, filename: str, request: Request):
    info = require_token(token)
    if not pin_ok(token, info, request):
        raise HTTPException(status_code=401, detail="PIN 필요")
    path = resolve_in_userdir(token, filename)
    if path is None:
        raise HTTPException(status_code=404, detail="Not found")
    media = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
    # FileResponse: 스트리밍 + 한글 파일명(Content-Disposition filename* 자동 처리)
    return FileResponse(path, media_type=media, filename=path.name)

@app.delete("/u/{token}/file/{filename}")
def delete_file(token: str, filename: str, request: Request):
    """다운로드 완료 후 클라이언트가 호출 → 해당 파일 서버에서 삭제(소비형)."""
    info = require_token(token)
    if not pin_ok(token, info, request):
        raise HTTPException(status_code=401, detail="PIN 필요")
    path = resolve_in_userdir(token, filename)
    if path is None:
        return JSONResponse({"deleted": False}, status_code=404)
    try:
        path.unlink()
        return {"deleted": True}
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

@app.get("/u/{token}/qr.png")
def qr_image(token: str):
    """이 토큰 업로드 URL의 QR을 PNG 이미지로 반환(브라우저에서 바로 열림)."""
    require_token(token)
    return Response(content=qr_png_bytes(upload_url(token)), media_type="image/png",
                    headers={"Cache-Control": "no-store"})

@app.get("/u/{token}/qr", response_class=HTMLResponse)
def qr_page(token: str):
    """QR + URL을 크게 보여주는 페이지(화면에 띄워 보여주기 좋음)."""
    info = require_token(token)
    url = upload_url(token)
    name = _html(info.get("name", ""))
    return HTMLResponse(f"""<!doctype html><html lang="ko"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>업로드 QR · {name}</title>{CSS}</head><body style="text-align:center">
<h1>📷 사진 업로드 QR</h1>
<div class="sub">{name}</div>
<div class="card" style="display:inline-block">
  <img src="/u/{token}/qr.png" alt="QR" style="width:min(80vw,340px);height:auto">
  <div style="margin-top:8px"><a href="{url}" style="word-break:break-all">{_html(url)}</a></div>
</div>
<div class="hint">폰 카메라로 이 QR을 스캔하세요.</div>
</body></html>""")

def _admin_enabled() -> bool:
    return bool(ADMIN_PASSWORD or ADMIN_KEY)

def _admin_secret_ok(secret: str) -> bool:
    for real in (ADMIN_PASSWORD, ADMIN_KEY):
        if real and secrets.compare_digest(secret or "", real):
            return True
    return False

def _admin_authed(request: Request) -> bool:
    return bool(request.session.get("admin"))

@app.get("/admin/login", response_class=HTMLResponse)
def admin_login_page(request: Request):
    if not _admin_enabled():
        raise HTTPException(status_code=404, detail="Not found")
    if _admin_authed(request):
        return RedirectResponse(url="/admin", status_code=303)
    return HTMLResponse(render_login_page())

@app.post("/admin/login", response_class=HTMLResponse)
def admin_login(request: Request, password: str = Form(...)):
    if not _admin_enabled():
        raise HTTPException(status_code=404, detail="Not found")
    if _admin_secret_ok(password):
        request.session["admin"] = True
        return RedirectResponse(url="/admin", status_code=303)
    return HTMLResponse(render_login_page("비밀번호가 올바르지 않습니다."), status_code=401)

@app.get("/admin/logout")
def admin_logout(request: Request):
    request.session.pop("admin", None)
    return RedirectResponse(url="/admin/login", status_code=303)

@app.get("/admin", response_class=HTMLResponse)
def admin(request: Request):
    """관리 콘솔: 토큰 발급/폐기 + QR + 설치파일. 로그인(세션) 필요."""
    if not _admin_enabled():
        raise HTTPException(status_code=404, detail="Not found")
    if not _admin_authed(request):
        return RedirectResponse(url="/admin/login", status_code=303)
    return HTMLResponse(render_admin_page())

@app.post("/admin/create")
def admin_create(request: Request, name: str = Form(...)):
    """관리 콘솔에서 이름 입력으로 새 토큰 발급."""
    if not _admin_authed(request):
        raise HTTPException(status_code=404, detail="Not found")
    issue_token((name or "").strip() or "(이름없음)")
    return RedirectResponse(url="/admin", status_code=303)

@app.post("/admin/revoke")
def admin_revoke(request: Request, token: str = Form(...)):
    """관리 콘솔에서 토큰 폐기(접근 차단)."""
    if not _admin_authed(request):
        raise HTTPException(status_code=404, detail="Not found")
    revoke_token(token)
    return RedirectResponse(url="/admin", status_code=303)

AGENT_PATH = BASE_DIR / "agent" / "SecureGateSync.ps1"

@app.get("/download/agent.ps1")
def download_agent():
    """통합 에이전트 스크립트 배포(설치파일이 내려받음). 비밀 아님 → 공개."""
    if not AGENT_PATH.is_file():
        raise HTTPException(status_code=404, detail="agent not found")
    return FileResponse(AGENT_PATH, media_type="text/plain", filename="SecureGateSync.ps1")

def build_installer_cmd(server: str, token: str) -> str:
    """개인별 설치 .cmd 생성. 더블클릭 → 에이전트 내려받기 + 설정 + 작업스케줄러 등록 + 시작.
    본문은 순수 ASCII, 실제 로직은 base64(-EncodedCommand)로 넣어 인코딩/따옴표 문제를 피한다."""
    ps = (
        "$ErrorActionPreference='Stop'\n"
        "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12\n"
        f"$server='{server}'\n"
        f"$token='{token}'\n"
        "$dir=Join-Path $env:LOCALAPPDATA 'SecureGateSync'\n"
        "New-Item -ItemType Directory -Force -Path $dir | Out-Null\n"
        "Write-Host '에이전트를 내려받는 중...'\n"
        "Invoke-WebRequest -Uri ($server+'/download/agent.ps1') -OutFile (Join-Path $dir 'SecureGateSync.ps1') -UseBasicParsing\n"
        "$cfg = \"@{`r`n  ServerBaseUrl = '$server'`r`n  Token = '$token'`r`n  IntervalSeconds = 3`r`n}\"\n"
        "[IO.File]::WriteAllText((Join-Path $dir 'SecureGateSync.config.psd1'), $cfg, (New-Object Text.UTF8Encoding($true)))\n"
        "$psf=Join-Path $dir 'SecureGateSync.ps1'\n"
        "$act=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"'+$psf+'\"')\n"
        "$trg=New-ScheduledTaskTrigger -AtLogOn\n"
        "$prn=New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited\n"
        "$set=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)\n"
        "Register-ScheduledTask -TaskName 'SecureGateSync' -Action $act -Trigger $trg -Principal $prn -Settings $set -Force | Out-Null\n"
        "Start-ScheduledTask -TaskName 'SecureGateSync'\n"
        "Write-Host ''\n"
        "Write-Host '설치 완료! 지금부터 자동으로 동작하며, 로그인할 때마다 자동 시작됩니다.' -ForegroundColor Green\n"
        "Write-Host '이 창은 닫아도 됩니다.'\n"
    )
    b64 = base64.b64encode(ps.encode("utf-16-le")).decode("ascii")
    return (
        "@echo off\r\n"
        "echo Installing SecureGateSync ...\r\n"
        f"powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand {b64}\r\n"
        "echo.\r\n"
        "pause\r\n"
    )

@app.get("/admin/installer")
def admin_installer(request: Request, token: str = ""):
    """개인별 설치 .cmd 다운로드. 관리 콘솔의 '설치파일' 버튼이 호출(로그인 필요)."""
    if not _admin_authed(request):
        raise HTTPException(status_code=404, detail="Not found")
    if get_token_info(token) is None:
        raise HTTPException(status_code=404, detail="Not found")
    cmd = build_installer_cmd(base_url(), token)
    return Response(content=cmd.encode("utf-8"), media_type="application/octet-stream",
                    headers={"Content-Disposition": 'attachment; filename="SecureGate-Setup.cmd"'})


# ──────────────────────────────────────────────────────────────
# HTML 렌더링 (단일 파일 유지 위해 인라인)
# ──────────────────────────────────────────────────────────────
def _fmt_size(n: int) -> str:
    size = float(n)
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024 or unit == "GB":
            return f"{int(size)}B" if unit == "B" else f"{size:.1f}{unit}"
        size /= 1024
    return f"{size:.1f}GB"

CSS = """
<style>
  :root { color-scheme: light dark; --blue:#2d6cdf; --bg:#ffffff; }
  @media (prefers-color-scheme: dark) { :root { --bg:#141414; } }
  * { box-sizing: border-box; }
  body { font-family: -apple-system, "Segoe UI", Roboto, "Malgun Gothic", sans-serif;
         margin: 0; padding: 16px; max-width: 760px; margin-inline: auto; }
  h1 { font-size: 1.25rem; margin: 0 0 4px; }
  .sub { color: #888; font-size: .85rem; margin-bottom: 16px; }
  .card { border: 1px solid #8883; border-radius: 12px; padding: 16px; margin-bottom: 16px; }
  button { font-size: 1rem; padding: 12px 16px; border-radius: 10px; border: 0;
           background: var(--blue); color: #fff; cursor: pointer; }
  button.secondary { background: #6b7280; }
  button.ghost { background: transparent; color: var(--blue); border: 1px solid #8886; }
  button:disabled { opacity: .5; cursor: default; }
  .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  a { color: var(--blue); }
  .meta { color: #999; font-size: .8rem; white-space: nowrap; }
  .hint { color:#999; font-size:.8rem; }
  .ok { color: #16a34a; } .err { color: #dc2626; }
  #msg { margin-top: 10px; font-size: .95rem; }

  /* 파일 목록 (다운로드 페이지) */
  ul.files { list-style: none; padding: 0; margin: 8px 0 0; }
  ul.files li { display: flex; justify-content: space-between; gap: 10px; align-items: center;
                padding: 12px 0; border-bottom: 1px solid #8882; }
  ul.files .fname { word-break: break-all; }

  /* 모바일 업로드 */
  .bigbtn { display:flex; flex-direction:column; align-items:center; justify-content:center; gap:6px;
            flex:1; min-width:130px; padding:22px 12px; font-size:1.05rem; border-radius:16px;
            border:2px dashed #8886; background:#8881; color:inherit; cursor:pointer; }
  .bigbtn .ico { font-size:2rem; }
  .thumbs { display:grid; grid-template-columns:repeat(auto-fill,minmax(84px,1fr)); gap:8px; margin-top:12px; }
  .thumbs .t { aspect-ratio:1; border-radius:10px; overflow:hidden; background:#8882; }
  .thumbs .t img { width:100%; height:100%; object-fit:cover; }
  .sticky { position:sticky; bottom:0; padding:12px 0; background:linear-gradient(to top, var(--bg) 72%, transparent); }
  .upbtn { width:100%; font-size:1.15rem; padding:16px; border-radius:14px; }

  /* 모달 */
  .modal { position:fixed; inset:0; background:#0009; display:none; align-items:center;
           justify-content:center; padding:16px; z-index:50; }
  .modal.open { display:flex; }
  .modal .box { background:var(--bg); color:inherit; border-radius:16px; padding:20px;
                max-width:360px; text-align:center; }
</style>
"""

def render_mobile_upload_page(token: str, info: dict) -> str:
    """모바일 전용 업로드 페이지 — 촬영/앨범 선택, 썸네일 미리보기, 큰 업로드 버튼."""
    name = _html(info.get("name", ""))
    return f"""<!doctype html><html lang="ko"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">
<title>사진 올리기 · {name}</title>{CSS}</head><body>
<h1>📷 사진 올리기</h1>
<div class="sub">{name} 님 · 찍거나 골라서 올리면 끝!</div>

<div class="card">
  <div class="row" style="gap:12px">
    <button class="bigbtn" id="btnCam" type="button"><span class="ico">📷</span>사진 촬영</button>
    <button class="bigbtn" id="btnGal" type="button"><span class="ico">🖼️</span>앨범에서 선택</button>
  </div>
  <input id="cam" type="file" accept="image/*" capture="environment" multiple hidden>
  <input id="gal" type="file" accept="image/*" multiple hidden>
  <div class="thumbs" id="thumbs"></div>
  <div id="msg"></div>
</div>

<div class="sticky">
  <button class="upbtn" id="up" type="button" disabled>업로드 (0장)</button>
</div>
<div style="text-align:center"><a href="/u/{token}/d" class="hint">PC에서 다운로드하기 →</a></div>

<script>
const token = {json_str(token)};
let picked = [];
const $ = s => document.querySelector(s);
const thumbs = $('#thumbs'), up = $('#up'), msg = $('#msg');

function refresh() {{
  thumbs.innerHTML = '';
  for (const f of picked) {{
    const d = document.createElement('div'); d.className = 't';
    const img = document.createElement('img'); img.src = URL.createObjectURL(f);
    d.appendChild(img); thumbs.appendChild(d);
  }}
  up.disabled = picked.length === 0;
  up.textContent = '업로드 (' + picked.length + '장)';
}}
function addFiles(list) {{ for (const f of list) picked.push(f); refresh(); }}
$('#btnCam').onclick = () => $('#cam').click();
$('#btnGal').onclick = () => $('#gal').click();
$('#cam').onchange = e => {{ addFiles(e.target.files); e.target.value = ''; }};
$('#gal').onchange = e => {{ addFiles(e.target.files); e.target.value = ''; }};

up.onclick = async () => {{
  if (!picked.length) return;
  const fd = new FormData();
  for (const f of picked) fd.append('files', f);
  up.disabled = true; msg.innerHTML = '<span class="hint">업로드 중...</span>';
  try {{
    const r = await fetch('/u/' + token + '/upload', {{ method: 'POST', body: fd }});
    const j = await r.json();
    let h = '<span class="ok">✅ ' + j.uploaded + '장 업로드 완료!</span>';
    if (j.errors && j.errors.length) h += '<br><span class="err">실패 ' + j.errors.length + '건: ' + j.errors.join(', ') + '</span>';
    msg.innerHTML = h;
    picked = []; refresh();
  }} catch (err) {{
    msg.innerHTML = '<span class="err">업로드 실패: ' + err + '</span>';
  }} finally {{ up.disabled = picked.length === 0; }}
}};
</script>
</body></html>"""


def render_desktop_download_page(token: str, info: dict) -> str:
    """PC 전용 다운로드 페이지 — 폴더 연결(자동저장), QR 모달, 다운로드 시 서버 삭제."""
    files = list_user_files(token)
    rows = ""
    for f in files:
        dt = datetime.fromtimestamp(f["mtime"]).strftime("%m-%d %H:%M")
        url = f"/u/{token}/file/{_url_quote(f['name'])}"
        rows += (f'<li class="filerow" data-url="{url}" data-name="{_html(f["name"])}">'
                 f'<span class="fname">{_html(f["name"])}</span>'
                 f'<span class="row" style="gap:10px">'
                 f'<span class="meta">{_fmt_size(f["size"])} · {dt}</span>'
                 f'<button class="one ghost" type="button">다운로드</button></span></li>')
    if not rows:
        rows = '<li class="hint">아직 업로드된 파일이 없습니다.</li>'
    name = _html(info.get("name", ""))
    delflag = "true" if DELETE_ON_DOWNLOAD else "false"
    qr_url = _html(upload_url(token))
    return f"""<!doctype html><html lang="ko"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>다운로드 · {name}</title>{CSS}</head><body>
<h1>📥 파일 다운로드</h1>
<div class="sub">{name} 전용 공간 · 본인 파일만 표시됩니다.</div>

<div class="card">
  <div class="row" style="justify-content:space-between">
    <div class="row" style="gap:8px">
      <button id="connect" class="ghost" type="button">📁 SecureGate 폴더 연결</button>
      <label class="hint" id="autoWrap" style="display:none"><input type="checkbox" id="auto" checked> 폴더로 자동 저장</label>
    </div>
    <div class="row" style="gap:8px">
      <button id="qrbtn" class="ghost" type="button">📱 모바일 QR</button>
      <button id="dlall" class="secondary" type="button">전체 다운로드</button>
    </div>
  </div>
  <div class="hint" id="folderStatus" style="margin-top:8px">폴더 미연결 — 다운로드는 브라우저 기본 폴더로 저장됩니다.</div>
</div>

<div class="card">
  <div class="row" style="justify-content:space-between">
    <strong>내 파일 (<span id="cnt">{len(files)}</span>)</strong>
    <span class="hint">다운로드하면 서버에서 자동 삭제됩니다.</span>
  </div>
  <ul class="files" id="list">{rows}</ul>
</div>

<div class="modal" id="qrmodal">
  <div class="box">
    <h1 style="font-size:1.1rem">📱 모바일 업로드 QR</h1>
    <div class="sub">{name} · 폰으로 스캔하세요</div>
    <img src="/u/{token}/qr.png" alt="QR" style="width:260px;max-width:70vw;height:auto">
    <div style="margin-top:8px;font-size:.8rem"><a href="{qr_url}">{qr_url}</a></div>
    <div style="margin-top:12px"><button id="qrclose" type="button">닫기</button></div>
  </div>
</div>

<script>
const token = {json_str(token)};
const DELETE_ON_DL = {delflag};
const $ = s => document.querySelector(s);
let dirHandle = null;
const supportsFS = 'showDirectoryPicker' in window;

if (!supportsFS) {{
  $('#connect').style.display = 'none';
  $('#folderStatus').textContent = '이 브라우저는 폴더 자동저장을 지원하지 않습니다. Chrome/Edge 권장 — 또는 브라우저 기본 다운로드 폴더를 SecureGate 감시 폴더로 설정하세요.';
}}

$('#connect').onclick = async () => {{
  try {{
    dirHandle = await window.showDirectoryPicker({{ mode: 'readwrite' }});
    $('#folderStatus').innerHTML = '✅ 연결됨: <b>' + dirHandle.name + '</b> — 다운로드가 이 폴더로 바로 저장됩니다.';
    $('#autoWrap').style.display = 'inline';
  }} catch (e) {{ /* 사용자가 취소 */ }}
}};

async function saveBlob(blob, name) {{
  if (dirHandle && $('#auto') && $('#auto').checked) {{
    const fh = await dirHandle.getFileHandle(name, {{ create: true }});
    const w = await fh.createWritable();
    await w.write(blob); await w.close();
  }} else {{
    const u = URL.createObjectURL(blob);
    const a = document.createElement('a'); a.href = u; a.download = name;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(u), 4000);
  }}
}}

async function downloadRow(li) {{
  const url = li.dataset.url, name = li.dataset.name;
  const btn = li.querySelector('.one'); if (btn) btn.disabled = true;
  try {{
    const res = await fetch(url);
    if (!res.ok) throw new Error('HTTP ' + res.status);
    const blob = await res.blob();
    await saveBlob(blob, name);
    if (DELETE_ON_DL) {{ try {{ await fetch(url, {{ method: 'DELETE' }}); }} catch (e) {{}} }}
    li.remove(); updateCount();
  }} catch (e) {{
    if (btn) btn.disabled = false;
    alert('다운로드 실패: ' + name + '\\n' + e);
  }}
}}
function updateCount() {{ $('#cnt').textContent = document.querySelectorAll('.filerow').length; }}

document.addEventListener('click', e => {{
  if (e.target.classList.contains('one')) downloadRow(e.target.closest('.filerow'));
}});
$('#dlall').onclick = async () => {{
  for (const li of [...document.querySelectorAll('.filerow')]) {{
    await downloadRow(li);
    await new Promise(r => setTimeout(r, 250));
  }}
}};

$('#qrbtn').onclick = () => $('#qrmodal').classList.add('open');
$('#qrclose').onclick = () => $('#qrmodal').classList.remove('open');
$('#qrmodal').onclick = e => {{ if (e.target.id === 'qrmodal') $('#qrmodal').classList.remove('open'); }};
</script>
</body></html>"""

def render_pin_page(token: str, error: str = "") -> str:
    err = f'<div class="err" style="margin-top:8px">{_html(error)}</div>' if error else ""
    return f"""<!doctype html><html lang="ko"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>PIN 입력</title>{CSS}</head><body>
<h1>🔒 PIN 입력</h1>
<div class="sub">이 공간은 PIN 보호되어 있습니다.</div>
<div class="card">
  <form method="post" action="/u/{token}/pin">
    <input type="password" name="pin" inputmode="numeric" autocomplete="off"
           style="width:100%;padding:12px;font-size:1.1rem;border-radius:10px;border:1px solid #8886" placeholder="PIN" autofocus>
    <div class="row" style="margin-top:12px"><button type="submit">확인</button></div>
    {err}
  </form>
</div></body></html>"""


def render_login_page(error: str = "") -> str:
    err = f'<div class="err" style="margin-top:8px">{_html(error)}</div>' if error else ""
    return f"""<!doctype html><html lang="ko"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>관리자 로그인</title>{CSS}</head><body>
<h1>🔐 관리자 로그인</h1>
<div class="sub">SecureGate 업로드 서버 · 관리 콘솔</div>
<div class="card">
  <form method="post" action="/admin/login">
    <input type="password" name="password" autocomplete="current-password" autofocus
      style="width:100%;padding:12px;font-size:1.05rem;border-radius:10px;border:1px solid #8886;background:transparent;color:inherit"
      placeholder="관리자 비밀번호">
    <div class="row" style="margin-top:12px"><button type="submit">로그인</button></div>
    {err}
  </form>
</div></body></html>"""


def render_admin_page() -> str:
    data = load_tokens()
    active = [(t, i) for t, i in data.items() if not i.get("revoked")]
    active.sort(key=lambda x: x[1].get("name", ""))
    cards = ""
    for t, i in active:
        url = upload_url(t)
        cards += (f'<div class="qcard">'
                  f'<img src="/u/{t}/qr.png" alt="QR">'
                  f'<div class="qname">{_html(i.get("name",""))}</div>'
                  f'<a class="qurl" href="{url}">{_html(url)}</a>'
                  f'<a class="qurl noprint" style="margin-top:6px;font-weight:600" '
                  f'href="/admin/installer?token={t}">⬇️ PC 설치파일(.cmd)</a>'
                  f'<form method="post" action="/admin/revoke" class="noprint" style="margin-top:8px" '
                  f'onsubmit="return confirm(\'이 토큰을 폐기할까요? 해당 사용자는 접근이 차단됩니다.\')">'
                  f'<input type="hidden" name="token" value="{t}">'
                  f'<button class="revoke" type="submit">폐기</button></form>'
                  f'</div>')
    if not active:
        cards = '<p class="hint">아직 발급된 토큰이 없습니다. 위에서 이름을 입력해 발급하세요.</p>'
    preset_value = _html(",".join(f"{t}={i.get('name','')}" for t, i in active))
    return f"""<!doctype html><html lang="ko"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>관리 콘솔</title>{CSS}
<style>
  .grid {{ display:grid; grid-template-columns:repeat(auto-fill,minmax(220px,1fr)); gap:14px; }}
  .qcard {{ border:1px solid #8883; border-radius:12px; padding:14px; text-align:center; page-break-inside:avoid; }}
  .qcard img {{ width:100%; max-width:200px; height:auto; }}
  .qname {{ font-weight:700; margin-top:8px; }}
  .qurl {{ font-size:.72rem; color:var(--blue); word-break:break-all; text-decoration:none; display:block; }}
  .revoke {{ font-size:.8rem; padding:5px 12px; background:transparent; color:#dc2626; border:1px solid #dc262688; border-radius:8px; }}
  textarea {{ width:100%; min-height:64px; font-family:monospace; font-size:.8rem; border-radius:8px;
              border:1px solid #8886; padding:8px; background:#8881; color:inherit; }}
  input[type=text] {{ padding:11px 12px; font-size:1rem; border-radius:10px; border:1px solid #8886;
                      background:transparent; color:inherit; }}
  @media print {{ .noprint {{ display:none; }} body {{ padding:0; }} }}
</style></head><body>
<div class="row noprint" style="justify-content:space-between; margin-bottom:12px">
  <h1 style="margin:0">🗂️ 관리 콘솔 <span class="hint">({len(active)}명)</span></h1>
  <div class="row" style="gap:8px">
    <button onclick="window.print()">QR 인쇄</button>
    <a href="/admin/logout"><button class="secondary" type="button">로그아웃</button></a>
  </div>
</div>

<div class="card noprint">
  <strong>➕ 새 토큰 발급</strong>
  <form method="post" action="/admin/create" class="row" style="margin-top:10px; gap:8px">
    <input type="text" name="name" placeholder="이름 (예: 홍길동)" required style="flex:1; min-width:160px">
    <button type="submit">발급</button>
  </form>
  <div class="hint" style="margin-top:8px">발급하면 아래 목록에 QR과 함께 나타납니다. 잃어버려도 여기서 다시 확인/재발급하면 됩니다.</div>
</div>

<div class="card noprint" style="border-color:#2d6cdf55">
  <strong>💻 사무실 PC 자동연동 설치</strong>
  <div class="hint" style="margin-top:6px">
    각 카드의 <b>⬇️ PC 설치파일(.cmd)</b> 을 받아 그 사람 PC에서 <b>더블클릭</b>만 하면 끝입니다.
    (에이전트 자동 설치 + 로그인 시 자동시작 등록 + 즉시 실행) → 이후 폰으로 올린 사진이
    3초 내 자동으로 SecureGate 전송 목록에 얹힙니다. <b>SecureGate 보내기 클릭만 사람이 합니다.</b>
    <br>※ 다운로드 시 브라우저/백신 경고가 뜨면 "실행/유지"를 선택하세요(내부 배포 파일).
  </div>
</div>

<div class="grid">{cards}</div>

<div class="card noprint" style="margin-top:16px">
  <strong>💾 토큰 영구 보존용 <code>PRESET_TOKENS</code></strong>
  <div class="hint" style="margin:6px 0 8px">
    Render 무료 플랜은 재시작 시 초기화됩니다. 아래 값을 복사해 Render의 <b>Environment → PRESET_TOKENS</b> 에
    붙여넣으면 재배포·재시작 후에도 같은 토큰이 유지됩니다. (유료 Disk를 붙였다면 불필요)
  </div>
  <textarea id="preset" readonly onclick="this.select()">{preset_value}</textarea>
  <div style="margin-top:8px"><button type="button" onclick="navigator.clipboard.writeText(document.getElementById('preset').value); this.textContent='복사됨!'">복사</button></div>
</div>
</body></html>"""


def _html(s: str) -> str:
    return (s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
             .replace('"', "&quot;"))

def _url_quote(s: str) -> str:
    from urllib.parse import quote
    return quote(s)

def json_str(s: str) -> str:
    return json.dumps(s)


# ──────────────────────────────────────────────────────────────
# CLI  (python app.py <command>)
# ──────────────────────────────────────────────────────────────
def _cli():
    import argparse
    ap = argparse.ArgumentParser(description="QR 업로드 서버 / 토큰 관리")
    sub = ap.add_subparsers(dest="cmd")

    p_issue = sub.add_parser("issue", help="새 사용자 토큰 발급")
    p_issue.add_argument("--name", required=True, help="사용자 이름/식별용 라벨")
    p_issue.add_argument("--pin", default=None, help="(선택) 개인 PIN")

    sub.add_parser("list", help="토큰 목록")

    p_rev = sub.add_parser("revoke", help="토큰 폐기(접근 차단)")
    p_rev.add_argument("token")

    p_qr = sub.add_parser("qr", help="토큰 URL/QR 출력")
    p_qr.add_argument("token")

    p_run = sub.add_parser("run", help="서버 실행")
    p_run.add_argument("--host", default=HOST)
    p_run.add_argument("--port", type=int, default=PORT)

    args = ap.parse_args()
    ensure_presets()   # PRESET_TOKENS 도 목록/QR 에 반영

    if args.cmd == "issue":
        token = issue_token(args.name, args.pin)
        url = upload_url(token)
        path = save_qr_png(token)
        print(f"\n발급 완료: {args.name}")
        print(f"  토큰 : {token}")
        print(f"  URL  : {url}")
        print(f"  QR   : {path}")
        print(qr_ascii(url))
        if args.pin:
            print("  (PIN 설정됨 — REQUIRE_PIN=true 일 때 적용)")

    elif args.cmd == "list":
        data = load_tokens()
        if not data:
            print("발급된 토큰 없음.")
            return
        for t, i in data.items():
            state = "폐기됨" if i.get("revoked") else "활성"
            pin = "PIN" if i.get("pin") else "-"
            print(f"[{state}] {i.get('name','')}  {pin}  {t}")
            print(f"         {upload_url(t)}")

    elif args.cmd == "revoke":
        print("폐기 완료." if revoke_token(args.token) else "해당 토큰 없음.")

    elif args.cmd == "qr":
        info = load_tokens().get(args.token)
        if not info:
            print("해당 토큰 없음.")
            return
        url = upload_url(args.token)
        print(f"{info.get('name','')}\n  {url}")
        print(qr_ascii(url))

    elif args.cmd == "run":
        import uvicorn
        uvicorn.run("app:app", host=args.host, port=args.port)

    else:
        ap.print_help()


if __name__ == "__main__":
    _cli()
