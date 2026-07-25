#!/usr/bin/env bash
# =============================================================================
# 릴리스절차 대응: "커밋 전 점검 목록 > Server~/ 포함"
#
# Server~ 는 Unity 가 임포트하지 않는 폴더라 에디터에서는 존재 여부가 보이지
# 않는다. git 에서 빠지면 UPM 설치 사용자에게 브리지 서버가 통째로 전달되지
# 않아 3단계(생성)가 전혀 동작하지 않는다.
# =============================================================================
CHECK_NAME="Server~ 필수 파일"
# shellcheck source=_lib.sh
. "$(dirname "$0")/_lib.sh"

SERVER_DIR="MCPToolTest/Assets/MCPTools/Editor/ComfyUIGenerator/Server~"

required=(
  "$SERVER_DIR/bridge_server.py"
  "$SERVER_DIR/variables.json"
  "$SERVER_DIR/workflows/GenerateImage.json"
  "$SERVER_DIR/workflows/GenerateImageFlux.json"
  "$SERVER_DIR/workflows/UI.json"
  "$SERVER_DIR/workflows/StyleChange.json"
  "$SERVER_DIR/workflows/Audio.json"
)

missing=""
for p in "${required[@]}"; do
  if [ -n "$(git ls-files -- "$p")" ]; then
    echo "  OK   $p"
  else
    echo "  FAIL $p (git 미추적)"
    missing="${missing}${missing:+, }$(basename "$p")"
  fi
done

if [ -n "$missing" ]; then
  fail "git 에 추적되지 않은 Server~ 필수 파일: $missing — 커밋에 포함시키세요"
fi

pass "Server~ 필수 파일 ${#required[@]}종 모두 git 에 추적 중 (bridge_server.py + variables.json + workflows 5종)"
