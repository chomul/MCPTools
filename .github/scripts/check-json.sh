#!/usr/bin/env bash
# =============================================================================
# 릴리스절차 대응: "커밋 전 점검 목록 > package.json 유효성" (+ 워크플로/매니페스트로 확대)
#
# 깨진 JSON 은 Unity 가 패키지 자체를 인식하지 못하게 하거나(package.json),
# 브리지 서버가 워크플로를 로드하지 못하게 만든다(Server~).
# =============================================================================
CHECK_NAME="JSON 유효성"
# shellcheck source=_lib.sh
. "$(dirname "$0")/_lib.sh"

SERVER_DIR="MCPToolTest/Assets/MCPTools/Editor/ComfyUIGenerator/Server~"

[ -n "$PYTHON" ] || fail "Python 3 실행 파일을 찾지 못했습니다"

targets=()
targets+=("MCPToolTest/Assets/MCPTools/package.json")
targets+=("$SERVER_DIR/variables.json")
targets+=("MCPToolTest/Packages/manifest.json")
# Server~/workflows/*.json 전체
while IFS= read -r f; do
  [ -n "$f" ] && targets+=("$f")
done < <(find "$SERVER_DIR/workflows" -maxdepth 1 -name '*.json' -type f 2>/dev/null | sort)

broken=""
checked=0
for f in "${targets[@]}"; do
  if [ ! -f "$f" ]; then
    broken="${broken}${broken:+$'\n'}없음: $f"
    continue
  fi
  if err="$("$PYTHON" -c 'import json,sys; json.load(open(sys.argv[1], encoding="utf-8"))' "$f" 2>&1)"; then
    checked=$((checked + 1))
    echo "  OK   $f"
  else
    # 파이썬 예외 마지막 줄만 (예: json.decoder.JSONDecodeError: Expecting ',' ...)
    broken="${broken}${broken:+$'\n'}파손: $f — $(printf '%s\n' "$err" | tail -n 1)"
    echo "  FAIL $f"
  fi
done

if [ -n "$broken" ]; then
  echo "$broken"
  first="$(printf '%s\n' "$broken" | head -n 1)"
  count="$(printf '%s\n' "$broken" | wc -l | tr -d ' ')"
  fail "JSON ${count}개 문제 (첫 항목: $first)"
fi

pass "JSON ${checked}개 파싱 OK (package.json / manifest.json / Server~ variables·workflows)"
