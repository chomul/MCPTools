#!/usr/bin/env bash
# =============================================================================
# 릴리스절차 대응: "버전 3중 동기 (가장 중요)"
#
#   1) MCPToolTest/Assets/AIAssetPipeline/package.json 의 version
#   2) MCPToolTest/Assets/AIAssetPipeline/Runtime/Data/AIAssetPipelineInfo.cs 의 Version 상수
#   3) git 태그 vX.Y.Z
#
# 1↔2 는 항상 비교한다. 3 은 "태그 push 이벤트"일 때만 (v 접두어를 뗀 뒤) 함께
# 3중 비교한다 — 일반 브랜치 push/PR 에는 아직 태그가 없기 때문.
#
# 사용법: check-version.sh [태그이름]   (인자는 로컬 시험용. CI 에서는 GITHUB_REF_* 사용)
# =============================================================================
CHECK_NAME="버전 3중 동기"
# shellcheck source=_lib.sh
. "$(dirname "$0")/_lib.sh"

PKG_JSON="MCPToolTest/Assets/AIAssetPipeline/package.json"
INFO_CS="MCPToolTest/Assets/AIAssetPipeline/Runtime/Data/AIAssetPipelineInfo.cs"

[ -f "$PKG_JSON" ] || fail "$PKG_JSON 파일이 없습니다"
[ -f "$INFO_CS" ]  || fail "$INFO_CS 파일이 없습니다"

[ -n "$PYTHON" ] || fail "Python 3 실행 파일을 찾지 못했습니다"

pkg_version="$("$PYTHON" -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8"))["version"])' "$PKG_JSON" 2>/dev/null)"
[ -n "$pkg_version" ] || fail "$PKG_JSON 에서 version 값을 읽지 못했습니다 (JSON 파손 또는 version 키 없음)"

# public const string Version = "0.2.0";
info_version="$(sed -n 's/.*const[[:space:]]\{1,\}string[[:space:]]\{1,\}Version[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' "$INFO_CS" | head -n 1)"
[ -n "$info_version" ] || fail "$INFO_CS 에서 Version 상수를 찾지 못했습니다"

# 태그: 인자로 받았거나(로컬 시험), 태그 push 이벤트일 때만 채워진다.
tag_name="${1:-}"
if [ -z "$tag_name" ] && [ "${GITHUB_REF_TYPE:-}" = "tag" ]; then
  tag_name="${GITHUB_REF_NAME:-}"
fi

echo "  package.json    version = $pkg_version"
echo "  AIAssetPipelineInfo.cs Version = $info_version"
[ -n "$tag_name" ] && echo "  git tag                 = $tag_name"

if [ "$pkg_version" != "$info_version" ]; then
  fail "package.json($pkg_version) 과 AIAssetPipelineInfo.Version($info_version) 이 다릅니다 — 두 곳을 같은 값으로 맞추세요"
fi

if [ -n "$tag_name" ]; then
  case "$tag_name" in
    v[0-9]*.[0-9]*.[0-9]*) ;;
    *) fail "태그 형식이 vX.Y.Z 가 아닙니다: $tag_name" ;;
  esac
  tag_version="${tag_name#v}"
  if [ "$tag_version" != "$pkg_version" ]; then
    fail "태그($tag_name) 와 package.json($pkg_version)·AIAssetPipelineInfo($info_version) 버전이 다릅니다"
  fi
  pass "3중 동기 OK — package.json / AIAssetPipelineInfo / 태그 모두 $pkg_version"
fi

pass "package.json ↔ AIAssetPipelineInfo.Version 일치 ($pkg_version) — 태그 push 가 아니므로 태그 비교는 건너뜁니다"
