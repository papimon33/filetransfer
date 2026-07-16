# -*- coding: utf-8 -*-
"""app.py 기능 검증 (TestClient). 실제 네트워크 없이 in-process."""
import os, tempfile, shutil, importlib, sys

tmp = tempfile.mkdtemp(prefix="qrtest_")
os.environ["UPLOAD_DIR"]  = os.path.join(tmp, "uploads")
os.environ["TOKENS_FILE"] = os.path.join(tmp, "tokens.json")
os.environ["QR_DIR"]      = os.path.join(tmp, "qr")
os.environ["MAX_FILE_MB"] = "1"          # 1MB로 낮춰 용량초과 테스트
os.environ["ALLOWED_EXT"] = "jpg,jpeg,png,heic,webp"
os.environ["REQUIRE_PIN"] = "false"
os.environ["BASE_URL"]    = "http://test.local"

import app as A
importlib.reload(A)
from fastapi.testclient import TestClient

results = []
def check(name, cond):
    results.append((name, bool(cond)))
    print(("PASS" if cond else "FAIL"), name)

# 토큰 2개 발급 (격리 테스트용)
tA = A.issue_token("사용자A")
tB = A.issue_token("사용자B")

with TestClient(A.app) as c:
    # 1) 잘못된/없는 토큰 → 404
    check("없는 토큰 404", c.get("/u/nonexistent-token").status_code == 404)
    check("토큰형식 위반 404", c.get("/u/..%2f..").status_code == 404)

    # 2) 정상 페이지
    r = c.get(f"/u/{tA}")
    check("A 페이지 200", r.status_code == 200)
    check("A 페이지에 업로드폼", "사진 업로드" in r.text)

    # 3) 업로드 (jpg 2장 + 한글파일명 + 금지확장자 txt)
    files = [
        ("files", ("현장사진.jpg", b"\xff\xd8\xff" + b"A"*1000, "image/jpeg")),
        ("files", ("보고서.png",   b"\x89PNG" + b"B"*2000, "image/png")),
        ("files", ("메모.txt",     b"hello", "text/plain")),          # 거부돼야 함
    ]
    r = c.post(f"/u/{tA}/upload", files=files)
    j = r.json()
    check("업로드 2장 성공", j["uploaded"] == 2)
    check("txt 거부됨", any("메모.txt" in e for e in j["errors"]))

    # 4) 용량 초과 (2MB > 1MB 제한)
    big = [("files", ("큰사진.jpg", b"C" * (2*1024*1024), "image/jpeg"))]
    r = c.post(f"/u/{tA}/upload", files=big)
    check("용량초과 거부", r.json()["uploaded"] == 0 and "용량 초과" in r.json()["errors"][0])

    # 5) 목록 — A에는 2개
    r = c.get(f"/u/{tA}/list")
    namesA = [f["name"] for f in r.json()["files"]]
    check("A 목록 2개", len(namesA) == 2)
    check("한글 파일명 보존", "현장사진.jpg" in namesA)

    # 6) 다운로드 — 원본 바이트 + 한글 Content-Disposition
    r = c.get(f"/u/{tA}/file/현장사진.jpg")
    check("다운로드 200", r.status_code == 200)
    check("원본 바이트 일치", r.content == b"\xff\xd8\xff" + b"A"*1000)
    cd = r.headers.get("content-disposition", "")
    check("한글 filename* 헤더", "filename*=utf-8''" in cd.lower())

    # 7) 격리 — B는 A 파일 안 보임 / 접근 불가
    r = c.get(f"/u/{tB}/list")
    check("B 목록 0개(격리)", len(r.json()["files"]) == 0)
    r = c.get(f"/u/{tB}/file/현장사진.jpg")
    check("B가 A파일 다운로드 404", r.status_code == 404)

    # 8) Path Traversal 차단
    for evil in ["..%2f..%2ftokens.json", "..%5c..%5ctokens.json", "....//tokens.json"]:
        rr = c.get(f"/u/{tA}/file/{evil}")
        check(f"traversal 차단 [{evil[:12]}]", rr.status_code == 404)

    # 9) 폐기된 토큰 → 404
    A.revoke_token(tB)
    check("폐기 토큰 404", c.get(f"/u/{tB}").status_code == 404)

    # 10) 저장 구조 물리 격리 확인
    ua = os.path.join(os.environ["UPLOAD_DIR"], tA)
    check("uploads/<A토큰>/ 폴더 분리", os.path.isdir(ua) and len(os.listdir(ua)) == 2)

# PIN 모드 별도 검증
os.environ["REQUIRE_PIN"] = "true"
importlib.reload(A)
tP = A.issue_token("PIN유저", pin="1234")
from fastapi.testclient import TestClient as TC2
with TC2(A.app) as c:
    r = c.get(f"/u/{tP}")
    check("PIN 게이트 노출", "PIN 입력" in r.text)
    r = c.get(f"/u/{tP}/list")
    check("PIN 없이 API 401", r.status_code == 401)
    r = c.post(f"/u/{tP}/pin", data={"pin": "0000"}, follow_redirects=False)
    check("틀린 PIN 401", r.status_code == 401)
    r = c.post(f"/u/{tP}/pin", data={"pin": "1234"}, follow_redirects=False)
    check("맞는 PIN 303 리다이렉트", r.status_code == 303)
    r = c.get(f"/u/{tP}/list")   # 세션 쿠키 유지됨
    check("PIN 통과 후 목록 200", r.status_code == 200)

shutil.rmtree(tmp, ignore_errors=True)
passed = sum(1 for _, ok in results if ok)
print(f"\n===== {passed}/{len(results)} PASS =====")
sys.exit(0 if passed == len(results) else 1)
