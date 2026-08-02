#!/usr/bin/env bash
# =============================================================================
# 릴리스절차 대응: "커밋 전 점검 목록 > 개인 값 미포함"
#
#  (a) AIAssetPipelineSettings.asset 커밋 여부 — .gitignore 대상인데 실수로 추가되면
#      개발자 PC 의 ComfyUI 주소·경로·모델명이 그대로 배포된다.
#  (b) 패키지 코드에 사용자 환경 절대 경로 하드코딩 여부.
#
# (b) 패턴을 좁힌 이유 — 순진하게 'C:\' 를 grep 하면 정상 코드가 걸린다:
#   * ComfyUIServerLauncher.cs : "짧은 경로(예: C:\\Unity\\<프로젝트명>)로 옮기거나"
#       -> 사용자에게 보여주는 '예시' 문구. 실제 경로가 아니다.
#   * ComfyUIServerLauncher.cs : /// "C:" 형태의 SystemDrive 값을 루트("C:\")로 보정
#       -> 주석. 드라이브 문자만 다루는 설명이다.
# 그래서 "드라이브 문자 + 실제 홈/설치 경로 세그먼트"가 이어질 때만 잡는다
# (C:\Users\, D:\Projects\, C:/Program Files/ 등). POSIX 는 /home/<이름>/,
# /Users/<이름>/ 만 본다. 주석 줄도 대상에 포함한다 — 주석에 박힌 개인 경로도
# 개인 값 노출이기 때문이다.
# =============================================================================
CHECK_NAME="개인 값·절대 경로"
# shellcheck source=_lib.sh
. "$(dirname "$0")/_lib.sh"

problems=""

# --- (a) 설정 에셋 커밋 여부 (릴리스절차의 명령과 동일) ------------------------------
# 파일명을 고정하지 않고 "설정 ScriptableObject 로 보이는 .asset" 을 전부 본다.
# v0.6.1 이하의 옛 이름(MCPToolSettings.asset)이 .gitignore 규칙만 이름이 바뀐 채
# 계속 추적되던 사고가 있었다 — 무시 규칙은 이미 추적 중인 파일을 되돌리지 못한다.
# 패키지에 배포용 .asset 은 하나도 없어야 하므로 이름을 좁히지 않는다.
settings_hits="$(git ls-files 'MCPToolTest/Assets/AIAssetPipeline/*' | grep -i '\.asset$')"
if [ -n "$settings_hits" ]; then
  echo "$settings_hits"
  problems="${problems}${problems:+ / }패키지에 .asset 이 커밋되어 있음(개인 설정 에셋일 수 있음 — .gitignore 확인)"
else
  echo "  OK   패키지에 커밋된 .asset 없음(설정 에셋 미추적)"
fi

# --- (b) 사용자 환경 절대 경로 하드코딩 ------------------------------------------
# 윈도우: (드라이브 문자):(\ 또는 / 또는 \\) + 홈/설치 성격의 세그먼트 + 구분자
WIN_PATH_RE='(^|[^A-Za-z0-9])[A-Za-z]:[\\/]{1,2}(Users|Documents and Settings|Program Files( \(x86\))?|ProgramData|Project|Projects|Desktop|Downloads|home|Windows[\\/]System32)([\\/]|$)'
# POSIX: /home/<이름>/ 또는 /Users/<이름>/
NIX_PATH_RE='(^|[^A-Za-z0-9_.~-])(/home/|/Users/)[A-Za-z0-9._-]+/'

path_hits="$(
  git ls-files -z -- \
      'MCPToolTest/Assets/AIAssetPipeline/*.cs' \
      'MCPToolTest/Assets/AIAssetPipeline/Editor/ComfyUIGenerator/Server~/*.py' \
    | xargs -0 -r grep -nIE "$WIN_PATH_RE|$NIX_PATH_RE"
)"

if [ -n "$path_hits" ]; then
  echo "$path_hits"
  hit_count="$(printf '%s\n' "$path_hits" | wc -l | tr -d ' ')"
  problems="${problems}${problems:+ / }절대 경로 하드코딩 의심 ${hit_count}건(위 목록)"
else
  scanned="$(git ls-files -- 'MCPToolTest/Assets/AIAssetPipeline/*.cs' 'MCPToolTest/Assets/AIAssetPipeline/Editor/ComfyUIGenerator/Server~/*.py' | wc -l | tr -d ' ')"
  echo "  OK   소스 ${scanned}개에 사용자 환경 절대 경로 없음"
fi

[ -n "$problems" ] && fail "$problems"

pass "패키지에 커밋된 .asset 없음 + 패키지 소스에 사용자 환경 절대 경로 없음"
