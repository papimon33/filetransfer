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
UPLOAD_DIR      = Path(_env("UPLOAD_DIR", str(BASE_DIR / "uploads"))).resolve()
TOKENS_FILE     = Path(_env("TOKENS_FILE", str(BASE_DIR / "tokens.json"))).resolve()
QR_DIR          = Path(_env("QR_DIR", str(BASE_DIR / "qrcodes"))).resolve()

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
ADMIN_KEY       = _env("ADMIN_KEY", "")   # 설정 시 /admin?key=... 로 전체 QR 대시보드 접근
SESSION_SECRET  = _env("SESSION_SECRET") or secrets.token_hex(32)
PRINT_QR_ON_START = _env("PRINT_QR_ON_START", "true").lower() in ("1", "true", "yes", "on")

UPLOAD_DIR.mkdir(parents=True, exist_ok=True)
QR_DIR.mkdir(parents=True, exist_ok=True)


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

@app.get("/u/{token}", response_class=HTMLResponse)
def page(token: str, request: Request):
    info = require_token(token)
    if not pin_ok(token, info, request):
        return HTMLResponse(render_pin_page(token))
    return HTMLResponse(render_main_page(token, info, request))

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

@app.get("/admin", response_class=HTMLResponse)
def admin(key: str = ""):
    """관리자 대시보드: 활성 토큰 전체의 QR/URL을 한 페이지에. ADMIN_KEY 로 보호."""
    if not ADMIN_KEY or not secrets.compare_digest(key, ADMIN_KEY):
        raise HTTPException(status_code=404, detail="Not found")   # 존재 자체를 숨김
    return HTMLResponse(render_admin_page())


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
  :root { color-scheme: light dark; }
  * { box-sizing: border-box; }
  body { font-family: -apple-system, "Segoe UI", Roboto, "Malgun Gothic", sans-serif;
         margin: 0; padding: 16px; max-width: 720px; margin-inline: auto; }
  h1 { font-size: 1.25rem; margin: 0 0 4px; }
  .sub { color: #888; font-size: .85rem; margin-bottom: 16px; }
  .card { border: 1px solid #8883; border-radius: 12px; padding: 16px; margin-bottom: 16px; }
  input[type=file] { width: 100%; padding: 10px 0; }
  button { font-size: 1rem; padding: 12px 16px; border-radius: 10px; border: 0;
           background: #2d6cdf; color: #fff; cursor: pointer; }
  button.secondary { background: #6b7280; }
  button:disabled { opacity: .5; cursor: default; }
  .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  ul.files { list-style: none; padding: 0; margin: 8px 0 0; }
  ul.files li { display: flex; justify-content: space-between; gap: 8px; align-items: center;
                padding: 10px 0; border-bottom: 1px solid #8882; }
  ul.files a { text-decoration: none; color: #2d6cdf; word-break: break-all; }
  .meta { color: #999; font-size: .8rem; white-space: nowrap; }
  #msg { margin-top: 10px; font-size: .95rem; }
  .ok { color: #16a34a; } .err { color: #dc2626; }
  .hint { color:#999; font-size:.8rem; }
</style>
"""

def render_main_page(token: str, info: dict, request: Request) -> str:
    files = list_user_files(token)
    rows = ""
    for f in files:
        dt = datetime.fromtimestamp(f["mtime"]).strftime("%m-%d %H:%M")
        url = f"/u/{token}/file/{_url_quote(f['name'])}"
        rows += (f'<li><a class="dl" href="{url}" download>{_html(f["name"])}</a>'
                 f'<span class="meta">{_fmt_size(f["size"])} · {dt}</span></li>')
    if not rows:
        rows = '<li class="hint">아직 업로드된 파일이 없습니다.</li>'
    name = _html(info.get("name", ""))
    return f"""<!doctype html><html lang="ko"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>사진 업로드 · {name}</title>{CSS}</head><body>
<h1>📷 사진 업로드</h1>
<div class="sub">{name} 전용 공간 · 이 화면의 파일은 본인만 볼 수 있습니다.</div>

<div class="card">
  <form id="f">
    <input id="file" type="file" name="files" accept="image/*" capture="environment" multiple>
    <div class="row" style="margin-top:10px">
      <button type="submit" id="btn">업로드</button>
      <span class="hint">여러 장 선택 가능 · 카메라 촬영 첨부 지원</span>
    </div>
  </form>
  <div id="msg"></div>
</div>

<div class="card">
  <div class="row" style="justify-content:space-between">
    <strong>내 파일 ({len(files)})</strong>
    <button class="secondary" id="dlall" type="button">전체 순차 다운로드</button>
  </div>
  <ul class="files" id="list">{rows}</ul>
  <div class="hint" style="margin-top:8px">※ PC에서 각 파일을 원본 그대로 저장하세요. 저장 폴더가 곧 SecureGate 감시 폴더입니다.</div>
</div>

<script>
const token = {json_str(token)};
const f = document.getElementById('f');
const btn = document.getElementById('btn');
const msg = document.getElementById('msg');

f.addEventListener('submit', async (e) => {{
  e.preventDefault();
  const input = document.getElementById('file');
  if (!input.files.length) {{ msg.innerHTML = '<span class="err">파일을 선택하세요.</span>'; return; }}
  const fd = new FormData();
  for (const file of input.files) fd.append('files', file);
  btn.disabled = true; msg.textContent = '업로드 중...';
  try {{
    const r = await fetch(`/u/${{token}}/upload`, {{ method: 'POST', body: fd }});
    const j = await r.json();
    let html = `<span class="ok">${{j.uploaded}}장 업로드 완료</span>`;
    if (j.errors && j.errors.length) html += `<br><span class="err">실패 ${{j.errors.length}}건: ${{j.errors.join(', ')}}</span>`;
    msg.innerHTML = html;
    setTimeout(() => location.reload(), 1200);
  }} catch (err) {{
    msg.innerHTML = '<span class="err">업로드 실패: ' + err + '</span>';
  }} finally {{ btn.disabled = false; }}
}});

document.getElementById('dlall').addEventListener('click', async () => {{
  const links = [...document.querySelectorAll('a.dl')];
  for (const a of links) {{
    const tmp = document.createElement('a');
    tmp.href = a.href; tmp.download = '';
    document.body.appendChild(tmp); tmp.click(); tmp.remove();
    await new Promise(res => setTimeout(res, 900));  // 브라우저 다중 다운로드 차단 완화
  }}
}});
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
                  f'</div>')
    if not active:
        cards = '<p>활성 토큰이 없습니다. <code>python app.py issue --name "이름"</code> 또는 PRESET_TOKENS 로 발급하세요.</p>'
    return f"""<!doctype html><html lang="ko"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>QR 대시보드</title>{CSS}
<style>
  .grid {{ display:grid; grid-template-columns:repeat(auto-fill,minmax(220px,1fr)); gap:14px; }}
  .qcard {{ border:1px solid #8883; border-radius:12px; padding:14px; text-align:center; page-break-inside:avoid; }}
  .qcard img {{ width:100%; max-width:200px; height:auto; }}
  .qname {{ font-weight:700; margin-top:8px; }}
  .qurl {{ font-size:.72rem; color:#2d6cdf; word-break:break-all; text-decoration:none; }}
  @media print {{ .noprint {{ display:none; }} body {{ padding:0; }} }}
</style></head><body>
<div class="row noprint" style="justify-content:space-between; margin-bottom:12px">
  <h1 style="margin:0">🗂️ QR 대시보드 <span class="hint">({len(active)}명)</span></h1>
  <button onclick="window.print()">인쇄</button>
</div>
<div class="hint noprint" style="margin-bottom:12px">각 QR을 현장 작업자에게 전달하세요. 인쇄해서 나눠줘도 됩니다.</div>
<div class="grid">{cards}</div>
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
