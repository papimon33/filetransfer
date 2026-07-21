# -*- coding: utf-8 -*-
"""확인 1) 위험 확장자 차단  2) 기존 PIN 없는 사용자 하위호환"""
import os, tempfile, shutil, importlib, sys

tmp = tempfile.mkdtemp(prefix="safetest_")
os.environ["UPLOAD_DIR"]  = os.path.join(tmp, "uploads")
os.environ["TOKENS_FILE"] = os.path.join(tmp, "tokens.json")
os.environ["QR_DIR"]      = os.path.join(tmp, "qr")
os.environ["BASE_URL"]    = "http://test.local"
os.environ["ENROLL_PIN_REQUIRED"] = "true"
os.environ.pop("ALLOWED_EXT", None)          # 운영 기본값 그대로 사용

import app as A
importlib.reload(A)
from fastapi.testclient import TestClient

results = []
def check(name, cond):
    results.append((name, bool(cond)))
    print(("PASS" if cond else "FAIL"), name)

print("=== 1) 위험 확장자 차단 ===")
DANGEROUS = ["a.exe", "a.ps1", "a.bat", "a.cmd", "a.vbs", "a.js", "a.jse", "a.wsf",
             "a.scr", "a.dll", "a.msi", "a.lnk", "a.reg", "a.hta", "a.jar",
             "a.com", "a.pif", "a.cpl", "a.docm", "a.xlsm", "a.pptm", "a.sh", "a.py"]
blocked = [f for f in DANGEROUS if not A.ext_ok(f)]
check(f"위험 확장자 {len(DANGEROUS)}종 전부 차단", len(blocked) == len(DANGEROUS))
if len(blocked) != len(DANGEROUS):
    print("   통과해버린 것:", [f for f in DANGEROUS if A.ext_ok(f)])

check("이중확장자 photo.jpg.exe 차단", not A.ext_ok("photo.jpg.exe"))
check("대문자 A.EXE 차단", not A.ext_ok("A.EXE"))
check("확장자 없음 차단", not A.ext_ok("malware"))
check("점만 있음 차단", not A.ext_ok("a."))
check("공백 우회 'a.exe ' 차단", not A.ext_ok("a.exe "))
check("정상 파일은 통과(jpg/pdf/hwp)", A.ext_ok("사진.jpg") and A.ext_ok("보고서.pdf") and A.ext_ok("문서.hwp"))

# 실제 업로드 경로에서도 차단되는지 (allowlist 실집행)
with TestClient(A.app) as c:
    tok = A.issue_token("안전테스트")
    files = [("files", ("evil.exe",      b"MZ\x90\x00", "application/octet-stream")),
             ("files", ("run.ps1",       b"Write-Host", "text/plain")),
             ("files", ("photo.jpg.exe", b"MZ\x90\x00", "image/jpeg")),
             ("files", ("정상.jpg",       b"\xff\xd8\xff" + b"x"*100, "image/jpeg"))]
    j = c.post(f"/u/{tok}/upload", files=files).json()
    check("업로드 API에서 위험파일 거부", j["uploaded"] == 1 and len(j["errors"]) == 3)
    names = [f["name"] for f in c.get(f"/u/{tok}/list").json()["files"]]
    check("서버에 위험파일 미저장", names == ["정상.jpg"])

print(f"\n   (참고) 현재 허용 확장자: {', '.join(sorted(A.ALLOWED_EXT))}")

print("\n=== 2) 기존 PIN 없는 사용자 하위호환 ===")
with TestClient(A.app) as c:
    # 구버전 사용자 재현: epin 필드 자체가 없는 문서
    legacy_tok = A.issue_token("구버전사용자")
    A.store.insert({"sabeon": "90001", "name": "구버전사용자", "token": legacy_tok,
                    "created": A.now_iso(), "revoked": False, "pin": None})   # epin 없음

    # (a) 이미 설치된 앱은 저장된 토큰으로 계속 동작해야 함 — 재등록 불필요
    r = c.get(f"/u/{legacy_tok}/list")
    check("기존 토큰으로 목록조회 정상", r.status_code == 200)
    up = c.post(f"/u/{legacy_tok}/upload",
                files=[("files", ("현장.jpg", b"\xff\xd8\xff" + b"y"*50, "image/jpeg"))]).json()
    check("기존 토큰으로 업로드 정상", up["uploaded"] == 1)
    r = c.get(f"/u/{legacy_tok}/file/현장.jpg")
    check("기존 토큰으로 다운로드 정상", r.status_code == 200)
    check("기존 토큰 QR 정상", c.get(f"/u/{legacy_tok}/qr.png").status_code == 200)

    # (b) 재등록할 때만 PIN을 정하게 되고, 토큰은 그대로 유지
    r = c.post("/api/enroll", data={"sabeon": "90001", "pin": "7788"})
    check("구버전 사번 재등록 성공", r.status_code == 200 and r.json()["ok"])
    check("재등록해도 같은 토큰 유지(설정 안깨짐)", r.json()["token"] == legacy_tok)
    # (c) 그 뒤부터는 잠김
    check("이후 다른 PIN 거부", c.post("/api/enroll", data={"sabeon": "90001", "pin": "0000"}).status_code == 403)
    check("정한 PIN 으로는 계속 사용", c.post("/api/enroll", data={"sabeon": "90001", "pin": "7788"}).status_code == 200)

shutil.rmtree(tmp, ignore_errors=True)
passed = sum(1 for _, ok in results if ok)
print(f"\n===== {passed}/{len(results)} PASS =====")
sys.exit(0 if passed == len(results) else 1)
