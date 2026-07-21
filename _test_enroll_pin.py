# -*- coding: utf-8 -*-
"""사번 등록 PIN 잠금 검증 — 남이 사번만 알고 토큰을 가로채지 못하는지."""
import os, tempfile, shutil, importlib, sys

tmp = tempfile.mkdtemp(prefix="pintest_")
os.environ["UPLOAD_DIR"]  = os.path.join(tmp, "uploads")
os.environ["TOKENS_FILE"] = os.path.join(tmp, "tokens.json")
os.environ["QR_DIR"]      = os.path.join(tmp, "qr")
os.environ["BASE_URL"]    = "http://test.local"
os.environ["ENROLL_PIN_REQUIRED"] = "true"

import app as A
importlib.reload(A)
from fastapi.testclient import TestClient

results = []
def check(name, cond):
    results.append((name, bool(cond)))
    print(("PASS" if cond else "FAIL"), name)

with TestClient(A.app) as c:
    def enroll(sabeon, pin):
        return c.post("/api/enroll", data={"sabeon": sabeon, "pin": pin})

    # 1) 신규 사번 — PIN 설정하며 발급
    r = enroll("14114", "1234")
    check("신규 발급 200", r.status_code == 200 and r.json()["ok"])
    tok = r.json()["token"]
    check("신규는 existed=False", r.json()["existed"] is False)

    # 2) 본인 재등록 — 같은 PIN → 같은 토큰
    r = enroll("14114", "1234")
    check("본인 재등록 성공", r.status_code == 200 and r.json()["ok"])
    check("같은 토큰 유지", r.json()["token"] == tok)
    check("existed=True", r.json()["existed"] is True)

    # 3) ★ 타인이 같은 사번 + 다른 PIN → 거부 (핵심)
    r = enroll("14114", "9999")
    check("타인 PIN 불일치 거부(403)", r.status_code == 403)
    check("토큰 미노출", "token" not in r.json())
    check("오류 메시지 안내", "PIN" in r.json().get("error", ""))

    # 4) PIN 형식 검증
    check("PIN 없음 거부", enroll("22222", "").status_code == 403)
    check("PIN 3자리 거부", enroll("22222", "123").status_code == 403)
    check("PIN 문자 거부", enroll("22222", "abcd").status_code == 403)
    check("PIN 7자리 거부", enroll("22222", "1234567").status_code == 403)

    # 5) 무차별 대입 차단 — 연속 실패 시 잠금
    A._epin_fail.clear()
    for i in range(A.EPIN_MAX_FAIL):
        enroll("14114", "0000")
    r = enroll("14114", "1234")          # 정답이어도 잠겨 있어야 함
    check("연속 실패 후 잠금", r.status_code == 403 and "잠" in r.json().get("error", ""))
    A._epin_fail.clear()
    r = enroll("14114", "1234")          # 잠금 해제 후 정상
    check("잠금 해제 후 정상", r.status_code == 200 and r.json()["token"] == tok)

    # 6) 다른 사번은 서로 영향 없음(격리)
    r2 = enroll("55555", "4321")
    check("다른 사번 별도 토큰", r2.status_code == 200 and r2.json()["token"] != tok)
    check("다른 사번 PIN 독립", enroll("55555", "1234").status_code == 403)

    # 7) 구버전(PIN 미설정) 사번 → 최초 등록 시 잠금(TOFU)
    legacy = A.issue_token("구버전")
    A.store.insert({"sabeon": "77777", "name": "구버전", "token": legacy,
                    "created": A.now_iso(), "revoked": False, "pin": None})
    r = enroll("77777", "5555")
    check("구버전 사번 최초 PIN 설정 성공", r.status_code == 200 and r.json()["token"] == legacy)
    check("구버전 사번 이후 잠김", enroll("77777", "6666").status_code == 403)

shutil.rmtree(tmp, ignore_errors=True)
passed = sum(1 for _, ok in results if ok)
print(f"\n===== {passed}/{len(results)} PASS =====")
sys.exit(0 if passed == len(results) else 1)
