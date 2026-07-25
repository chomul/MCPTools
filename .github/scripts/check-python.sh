#!/usr/bin/env bash
# =============================================================================
# 릴리스절차 대응: "Server~/ 포함" 항목의 실효성 보강 — 브리지 서버 문법 검사
#
# bridge_server.py 는 Unity 가 임포트하지 않아 컴파일 오류가 릴리스 전에 드러나지
# 않는다. 문법이 깨지면 3단계(생성)가 통째로 죽으므로 py_compile 로 확인한다.
# py_compile 이 만든 __pycache__ 는 남기지 않는다.
# =============================================================================
CHECK_NAME="Python 문법"
# shellcheck source=_lib.sh
. "$(dirname "$0")/_lib.sh"

PY="MCPToolTest/Assets/MCPTools/Editor/ComfyUIGenerator/Server~/bridge_server.py"
[ -f "$PY" ] || fail "$PY 파일이 없습니다"
[ -n "$PYTHON" ] || fail "Python 3 실행 파일을 찾지 못했습니다"

CACHE_DIR="$(dirname "$PY")/__pycache__"
# 실행 전에 이미 있던 __pycache__ 는 남의 것이므로 건드리지 않는다 (CI 는 항상 없음).
had_cache=0
[ -d "$CACHE_DIR" ] && had_cache=1

out="$("$PYTHON" -m py_compile "$PY" 2>&1)"
status=$?

[ "$had_cache" -eq 0 ] && rm -rf "$CACHE_DIR"

if [ "$status" -ne 0 ]; then
  echo "$out"
  # py_compile 출력의 마지막 줄이 대개 "SyntaxError: ..." 이다.
  reason="$(printf '%s\n' "$out" | tail -n 1)"
  fail "bridge_server.py 문법 오류 — $reason"
fi

pass "bridge_server.py 문법 OK ($("$PYTHON" --version 2>&1))"
