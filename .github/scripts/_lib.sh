#!/usr/bin/env bash
# =============================================================================
# CI 점검 스크립트 공통 헬퍼
#
# 각 점검 스크립트는 맨 위에서 CHECK_NAME 을 지정한 뒤 이 파일을 source 한다.
# 실패는 항상 "한 줄 요약"으로 먼저 출력해, 릴리스 담당자가 로그를 파고들지
# 않아도 어떤 점검이 왜 실패했는지 바로 보이게 한다.
#   - GitHub Actions 주석(::error::)  -> 실행 페이지 상단에 노출
#   - $GITHUB_STEP_SUMMARY 표 한 줄   -> 실행 요약 페이지에 누적
# =============================================================================

set -uo pipefail

# 스크립트를 어디서 실행하든 저장소 루트 기준으로 동작하게 맞춘다.
REPO_ROOT="$(git rev-parse --show-toplevel)" || {
  echo "FAIL: git 저장소 안에서 실행해야 합니다" >&2
  exit 1
}
cd "$REPO_ROOT" || exit 1

CHECK_NAME="${CHECK_NAME:-점검}"

# 사용할 Python 3 실행 파일. CI(ubuntu-latest)는 python3 이고,
# 릴리스 담당자가 Windows Git Bash 에서 돌릴 때는 python3 가 Microsoft Store
# 스텁이라 실행되지 않으므로 python 으로 폴백한다.
_detect_python() {
  for c in python3 python py; do
    command -v "$c" >/dev/null 2>&1 || continue
    "$c" -c 'import sys; raise SystemExit(0 if sys.version_info[0] == 3 else 1)' >/dev/null 2>&1 && {
      printf '%s' "$c"
      return 0
    }
  done
  return 1
}
PYTHON="$(_detect_python)"

# 실행 요약 페이지에 표 한 줄을 추가한다 (로컬 실행 시에는 조용히 무시).
_summary_row() {
  [ -n "${GITHUB_STEP_SUMMARY:-}" ] || return 0
  printf '| %s | %s | %s |\n' "$1" "$CHECK_NAME" "$2" >> "$GITHUB_STEP_SUMMARY"
}

# fail "한 줄 사유" — 점검 실패. 스크립트를 종료한다.
fail() {
  echo "FAIL [$CHECK_NAME] $1"
  echo "::error title=$CHECK_NAME::$1"
  _summary_row "FAIL" "$1"
  exit 1
}

# pass "한 줄 결과" — 점검 통과.
pass() {
  echo "PASS [$CHECK_NAME] $1"
  _summary_row "OK" "$1"
  exit 0
}
