#!/usr/bin/env bash
# =============================================================================
# 릴리스절차 대응: "커밋 전 점검 목록 > .meta 누락 없음"
#
# 패키지 에셋은 GUID(.meta)로 참조되므로 .meta 가 빠지면 사용자 프로젝트에서
# asmdef/에셋 참조가 깨진다. docs/릴리스절차.md 에 적힌 명령을 그대로 사용한다.
#
# CI 는 깨끗한 checkout 이라 "작업 트리 == 추적 파일"이므로,
# 파일만 커밋하고 .meta 는 커밋하지 않은 경우도 여기서 잡힌다.
# =============================================================================
CHECK_NAME=".meta 누락"
# shellcheck source=_lib.sh
. "$(dirname "$0")/_lib.sh"

# docs/릴리스절차.md 의 명령 (결과가 비어야 정상)
missing="$(
  git ls-files 'MCPToolTest/Assets/MCPTools/*' | grep -v '\.meta$' | grep -v 'Server~' \
    | while read -r f; do [ -f "$f.meta" ] || echo "META 누락: $f"; done
)"

if [ -n "$missing" ]; then
  echo "$missing"
  count="$(printf '%s\n' "$missing" | grep -c '^META 누락:')"
  fail ".meta 가 없는 패키지 파일 ${count}개 (위 목록) — Unity 에디터를 한 번 열어 .meta 를 생성시킨 뒤 커밋하세요"
fi

checked="$(git ls-files 'MCPToolTest/Assets/MCPTools/*' | grep -v '\.meta$' | grep -cv 'Server~')"
pass "패키지 추적 파일 ${checked}개 모두 .meta 짝이 맞습니다"
