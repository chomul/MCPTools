# -*- coding: utf-8 -*-
"""AIAssetPipeline 브리지 서버.

Unity 에디터와 ComfyUI 사이의 로컬 중간 서버입니다. 원본 워크플로 JSON을
그대로 로드해 요청 시점에 노드 inputs 값만 덮어쓰고, 시드를 바꿔가며
ComfyUI에 여러 번 큐잉한 뒤 완료를 폴링합니다.

표준 라이브러리만 사용합니다 (외부 의존성 없음).

실행:
    python bridge_server.py --host 127.0.0.1 --port 8189 --comfy-url http://127.0.0.1:8188
"""

import sys

if sys.version_info < (3, 7):
    sys.stderr.write(
        "[bridge] Python 3.7 이상이 필요합니다. 현재 실행 중인 버전: %s\n"
        % sys.version.split()[0]
    )
    sys.stderr.write(
        "[bridge] python.org에서 Python 3.7 이상을 설치한 뒤(설치 시 "
        "'Add python.exe to PATH' 체크), Unity의 Tools/AI Asset Pipeline/Settings에서 "
        "[Python 자동 탐지]를 누르거나 python.exe 절대 경로를 지정해주세요.\n"
    )
    sys.exit(1)

import argparse
import copy
import errno
import json
import os
import random
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# 브리지 서버 자체 버전. /health 응답에 실려 나가며, Unity가 다른 위치·다른 버전의
# 브리지에 붙었는지 판별하는 데 씁니다. 브리지 API가 바뀌면 함께 올립니다.
# 0.3.0: Host 헤더 검증 + 요청 본문/count 상한 추가 (동작 제약이 늘어난 minor 상향).
# 0.4.0: 완료 폴링 지연 축소 + /object_info TTL 캐시 + 워크플로 JSON mtime 캐시.
#        GET /workflows?refresh=1 로 캐시를 수동 무효화하는 경로가 새로 생겼고(기능 추가),
#        /object_info 결과가 최대 60초 캐시되어 "지금 막 추가한 모델"이 바로 보이지 않을 수
#        있으므로(관측 가능한 동작 변화) minor 상향.
BRIDGE_VERSION = "0.4.0"

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
SCRIPT_PATH = os.path.abspath(__file__)
WORKFLOWS_DIR = os.path.join(BASE_DIR, "workflows")
VARIABLES_PATH = os.path.join(BASE_DIR, "variables.json")

# 사용자 오버라이드 폴더 (--user-dir 인자로 설정, 빈 값이면 미사용).
# "<user-dir>/workflows/<이름>.json"과 "<user-dir>/variables.json"이 있으면
# 패키지 동봉본(WORKFLOWS_DIR/VARIABLES_PATH)보다 우선합니다. UPM(읽기 전용 패키지)
# 설치에서 사용자가 워크플로/변수를 수정할 수 있게 하는 장치입니다.
USER_DIR = ""

COMFY_URL = "http://127.0.0.1:8188"
CLIENT_ID = uuid.uuid4().hex

# 실제로 바인딩한 주소/포트 (main에서 설정). /shutdown이 로컬 전용인지 판단하고,
# Host 헤더 검증에서 "우리 서비스 포트"를 대조하는 데 씁니다.
BIND_HOST = "127.0.0.1"
BIND_PORT = 8189

# Host 헤더 검증 활성화 여부 (main에서 바인딩 주소를 보고 결정).
#
# 정책 결정(Task 10 R7):
#   - 루프백(127.0.0.1/localhost/::1)에 바인딩한 기본 구성에서는 Host 검증을 켠다.
#     브라우저는 <img>/fetch 같은 단순 요청을 프리플라이트 없이 로컬 주소로 보낼 수 있고,
#     응답을 못 읽어도 /generate·/free·/shutdown 같은 부작용은 그대로 발생한다.
#     또 공격자 도메인의 DNS를 127.0.0.1로 돌리는 DNS rebinding에서는 Host 헤더가
#     공격자 도메인으로 남으므로, Host를 루프백 이름으로 제한하면 둘 다 막힌다.
#   - --host 로 루프백이 아닌 주소에 바인딩한 경우는 운영자가 "다른 기기에서 접속시키겠다"고
#     의도적으로 외부 노출한 상황이다. 이때 접속에 쓰일 호스트명(사설 IP·머신 이름·역방향
#     DNS 이름 등)을 서버가 알 방법이 없어 허용 목록을 만들 수 없다. 정상 사용을 깨뜨리지
#     않기 위해 Host 검증을 끄고, 대신 기동 시 경고를 1회 출력해 위험을 알린다.
HOST_CHECK_ENABLED = True

# Host 헤더의 호스트명 부분으로 허용하는 값 (소문자 비교).
ALLOWED_HOST_NAMES = ("127.0.0.1", "localhost", "::1")

# 요청 본문 상한. 브리지의 JSON 엔드포인트는 워크플로 변수 정도만 받으므로 1 MiB면 충분하다.
# /upload만 예외로, 참조 이미지 원본을 그대로 실어 보내므로 훨씬 큰 상한을 쓴다.
MAX_BODY_BYTES = 1048576          # 1 MiB
MAX_UPLOAD_BODY_BYTES = 67108864  # 64 MiB (/upload 전용)

# /generate 의 count 상한. 오타 하나로 수천 건이 ComfyUI 큐에 들어가는 것을 막는다.
MAX_GENERATE_COUNT = 32

# jobId -> job dict (락으로 보호)
JOBS = {}
JOBS_LOCK = threading.Lock()

# /history 완료 폴링 간격(초).
#
# 근거(Task 8 S4): 이 값은 "ComfyUI가 실제로 끝낸 시각 → 브리지가 완료를 알아채는 시각"의
# 상한이다. localhost /history는 이미 끝난 prompt의 작은 JSON을 돌려주는 저비용 조회라
# 간격을 줄여도 ComfyUI 부하가 사실상 늘지 않는다. 0.3초로 두면 감지 지연이 최대 0.3초
# (평균 0.15초)로, 기존 1.0초 대비 배치당 약 0.85초를 줄이면서 요청 수는 후보 수 × 초당
# 3.3회 수준에 머문다. 0.25초 미만은 감지 이득이 100ms 미만인 데 비해 요청 수만 늘어
# 채택하지 않았다.
POLL_INTERVAL_SEC = 0.3
JOB_TIMEOUT_SEC = 600.0

# /object_info 응답 캐시 유지 시간(초). 설치된 모델/커스텀 노드 목록은 ComfyUI를 재시작하거나
# 파일을 추가하기 전까지 바뀌지 않으므로 짧은 TTL로 충분하다. 사용자가 ComfyUI에 모델을
# 추가한 직후라면 최대 이 시간만큼 목록이 낡을 수 있고, 즉시 갱신하려면
# GET /workflows?refresh=1 또는 POST /free 로 캐시를 비우면 된다.
OBJECT_INFO_TTL_SEC = 60.0

# _OBJECT_INFO_CACHE = (조회 시각(monotonic), object_info dict) 또는 None.
# 실패(None) 응답은 캐시하지 않는다 — ComfyUI 미기동 상태에서 comfyReachable=false가
# 캐시 때문에 고착되면 ComfyUI를 켠 뒤에도 최대 TTL 동안 미연결로 보이기 때문이다.
_OBJECT_INFO_CACHE = None
_OBJECT_INFO_LOCK = threading.Lock()

# 워크플로 JSON 파싱 결과 캐시: 절대 경로 -> ((st_mtime_ns, st_size), 파싱된 dict).
# mtime/size 확인은 매 호출마다 하고 파싱만 건너뛰므로, 사용자가 JSON을 편집하면 즉시 반영된다.
# 캐시에 담긴 dict는 절대 그대로 반환하지 않는다 (load_workflow가 deepcopy 사본을 준다).
_WORKFLOW_CACHE = {}
_WORKFLOW_CACHE_LOCK = threading.Lock()

# 이 길이 이상의 경로는 Windows 기본 최대 경로 길이(MAX_PATH = 260자)에 근접한 것으로 보고
# 파일 열기 실패 시 "경로가 너무 길다"는 안내를 덧붙입니다. UPM(git URL) 설치본은
# Library/PackageCache/<패키지명>@<40자 커밋 해시>/ 아래에 놓여 경로가 특히 길어집니다.
LONG_PATH_THRESHOLD = 250


# ──────────────────────────── 경로 길이 진단 ────────────────────────────

def path_hint(path, error=None):
    """경로 길이 문제로 의심될 때만 원인·조치 안내 문구를 반환합니다.

    판정: 절대 경로 길이가 LONG_PATH_THRESHOLD 이상이거나, 예외의 errno가
    ENAMETOOLONG인 경우. 그 외에는 빈 문자열을 반환하므로 정상 경로에서는
    기존 메시지가 그대로 유지됩니다.
    """
    if not path:
        return ""
    try:
        length = len(os.path.abspath(path))
    except Exception:
        length = len(path)

    name_too_long = getattr(error, "errno", None) == errno.ENAMETOOLONG
    if length < LONG_PATH_THRESHOLD and not name_too_long:
        return ""

    return (" 파일 경로가 너무 길어 열지 못했을 수 있습니다 (경로 길이 %d자, Windows 기본 한계 260자). "
            "Unity 프로젝트를 더 짧은 경로로 옮기거나, Windows의 긴 경로 지원을 켜주세요 "
            "(레지스트리 HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem 의 "
            "LongPathsEnabled를 1로 설정한 뒤 재부팅)." % length)


def hinted_error(error, hint):
    """원본 예외와 같은 종류로, 안내 문구를 덧붙인 새 예외를 만듭니다.

    예외 종류를 보존해야 호출 측의 기존 처리 흐름(FileNotFoundError → HTTP 404 등)이
    바뀌지 않습니다. 종류 재생성이 불가능하면 OSError로 대체합니다.
    """
    message = "%s%s" % (error, hint)
    try:
        return type(error)(message)
    except Exception:
        return OSError(message)


def read_json_file(path):
    """UTF-8 JSON 파일을 읽습니다.

    파일을 열지 못해 OSError(파일 없음 포함)가 나면 path_hint가 경로 길이 문제로
    판정할 때만 안내를 덧붙여 같은 종류의 예외로 다시 던집니다. 그 외에는 원본 예외를
    그대로 전달해 기존 오류 응답(상태 코드·JSON 키)을 유지합니다.
    """
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except OSError as e:
        hint = path_hint(path, e)
        if not hint:
            raise
        raise hinted_error(e, hint)


# ──────────────────────────── 워크플로/변수 로드 ────────────────────────────

def user_workflows_dir():
    """사용자 오버라이드 workflows 폴더 경로를 반환합니다 (--user-dir 미지정 시 빈 문자열)."""
    return os.path.join(USER_DIR, "workflows") if USER_DIR else ""


def user_dir_active():
    """--user-dir가 지정되었고 폴더가 실제로 존재하는지 여부입니다."""
    return bool(USER_DIR) and os.path.isdir(USER_DIR)


def load_variables_manifest():
    """variables.json(워크플로별 조정 변수 매니페스트)을 읽습니다.

    "<user-dir>/variables.json"이 존재하면 그 파일을 통째로 사용합니다
    (병합하지 않음 — 예측 가능성 우선). 없으면 패키지 동봉본을 사용합니다.
    파일을 열지 못하면 경로 길이 문제일 때만 원인 안내가 예외 메시지에 덧붙습니다.
    """
    if USER_DIR:
        user_path = os.path.join(USER_DIR, "variables.json")
        if os.path.isfile(user_path):
            return read_json_file(user_path)
    return read_json_file(VARIABLES_PATH)


def list_workflow_names():
    """워크플로 이름 목록(확장자 제외)을 반환합니다.

    패키지 동봉 폴더와 사용자 오버라이드 폴더("<user-dir>/workflows")의 합집합이며,
    같은 이름은 1개만 나옵니다 (로드는 사용자 쪽 우선). 폴더가 없으면 조용히 건너뜁니다.
    """
    names = set()
    for directory in (WORKFLOWS_DIR, user_workflows_dir()):
        if not directory or not os.path.isdir(directory):
            continue
        try:
            entries = os.listdir(directory)
        except OSError as e:
            # 경로 길이 문제로 판정될 때만 안내를 덧붙이고, 그 외에는 원본 예외를 그대로 올린다.
            hint = path_hint(directory, e)
            if not hint:
                raise
            raise hinted_error(e, hint)
        for entry in entries:
            if entry.lower().endswith(".json"):
                names.add(os.path.splitext(entry)[0])
    return sorted(names)


def invalidate_workflow_cache():
    """워크플로 JSON 파싱 캐시를 비웁니다 (수동 새로고침 경로에서 호출)."""
    with _WORKFLOW_CACHE_LOCK:
        _WORKFLOW_CACHE.clear()


def read_workflow_json(path):
    """워크플로 JSON을 읽되, 파일이 그대로면 이전 파싱 결과를 재사용합니다.

    반환값은 **캐시가 보유한 원본 객체**이므로 호출 측이 변형해서는 안 됩니다.
    외부에는 load_workflow()가 deepcopy 사본만 내보냅니다.

    파일 수정 감지는 (st_mtime_ns, st_size)로 하며 매 호출마다 stat을 확인합니다.
    stat이 실패하면 캐시를 쓰지 않고 항상 다시 읽습니다(기존 예외 흐름 유지).
    """
    try:
        info = os.stat(path)
        stamp = (info.st_mtime_ns, info.st_size)
    except OSError:
        return read_json_file(path)

    with _WORKFLOW_CACHE_LOCK:
        entry = _WORKFLOW_CACHE.get(path)
        if entry is not None and entry[0] == stamp:
            return entry[1]

    parsed = read_json_file(path)
    with _WORKFLOW_CACHE_LOCK:
        _WORKFLOW_CACHE[path] = (stamp, parsed)
    return parsed


def load_workflow(name):
    """원본 워크플로 JSON을 그대로 로드합니다 (구조 변경 없음).

    "<user-dir>/workflows/<이름>.json"이 있으면 그것을 우선 로드하고,
    없으면 패키지 동봉 폴더에서 로드합니다.
    파일이 보이지 않거나 열리지 않을 때, 경로 길이가 임계값을 넘으면
    "파일 없음"과 구분되도록 경로 길이 안내를 메시지에 덧붙입니다
    (Windows는 MAX_PATH를 넘는 경로를 오류 대신 "없음"으로 돌려주기도 합니다).

    파싱 결과는 파일 mtime 기준으로 캐시되지만, 호출 측은 이 반환값을 자유롭게
    변형합니다(apply_variables·set_seed가 노드 inputs를 덮어씀). 따라서 항상
    deepcopy 사본을 돌려주어 캐시 오염을 원천 차단합니다.
    """
    safe = os.path.basename(name)
    path = ""
    user_dir = user_workflows_dir()
    if user_dir:
        user_path = os.path.join(user_dir, safe + ".json")
        if os.path.isfile(user_path):
            path = user_path
    if not path:
        path = os.path.join(WORKFLOWS_DIR, safe + ".json")
    if not os.path.isfile(path):
        raise FileNotFoundError("워크플로를 찾을 수 없습니다: %s%s" % (name, path_hint(path)))
    return copy.deepcopy(read_workflow_json(path))


def coerce_value(var_type, value):
    """매니페스트 타입에 맞게 값을 변환합니다."""
    if var_type == "int":
        return int(round(float(value)))
    if var_type == "float":
        return float(value)
    if var_type == "bool":
        if isinstance(value, str):
            return value.strip().lower() in ("1", "true", "yes", "on")
        return bool(value)
    # string / image → 문자열 그대로
    return "" if value is None else str(value)


def apply_variables(workflow, workflow_name, variables, manifest):
    """{"nodeId.field": value} 형태의 변수를 노드 inputs에 덮어씁니다.

    매니페스트에 있는 항목은 타입 변환을 거치고, 없는 항목도 관대하게 허용합니다.
    """
    type_map = {}
    for var in manifest.get(workflow_name, []):
        type_map["%s.%s" % (var["nodeId"], var["field"])] = var.get("type", "string")

    errors = []
    for key, value in (variables or {}).items():
        if "." not in key:
            errors.append("변수 키 형식 오류 (nodeId.field 필요): %s" % key)
            continue
        node_id, field = key.split(".", 1)
        node = workflow.get(node_id)
        if node is None or "inputs" not in node:
            errors.append("워크플로에 노드가 없습니다: #%s (%s)" % (node_id, key))
            continue
        var_type = type_map.get(key)
        try:
            if var_type:
                node["inputs"][field] = coerce_value(var_type, value)
            else:
                node["inputs"][field] = value
        except (TypeError, ValueError):
            errors.append("변수 값 변환 실패: %s = %r (type=%s)" % (key, value, var_type))
    return errors


def _resolve_constant_bool(workflow, value):
    """스위치 입력 값을 상수 bool로 해석합니다.

    bool 리터럴이거나 PrimitiveBoolean 노드로의 링크([nodeId, 0])면 그 값을,
    상수로 확정할 수 없으면 None을 반환합니다.
    """
    if isinstance(value, bool):
        return value
    if isinstance(value, list) and len(value) == 2:
        src = workflow.get(str(value[0]))
        if isinstance(src, dict) and src.get("class_type") == "PrimitiveBoolean":
            flag = (src.get("inputs") or {}).get("value")
            if isinstance(flag, bool):
                return flag
    return None


def fold_constant_switches(workflow):
    """상수 불리언으로 분기가 확정된 ComfySwitchNode를 접어 선택된 입력으로 직결합니다.

    스위치 출력([nodeId, 0])을 참조하는 모든 입력을 선택된 분기(on_true/on_false)
    값으로 바꾼 뒤 스위치 노드를 제거합니다. 이렇게 하면 선택되지 않은 분기가
    그래프에서 끊어져 prune_unreachable_nodes()로 제거될 수 있고, 예를 들어
    '참조 이미지 사용'이 꺼진 UI 워크플로는 LoadImage 없이도 실행됩니다.
    스위치가 다른 스위치를 물고 있는 경우를 위해 변화가 없을 때까지 반복합니다.
    """
    changed = True
    while changed:
        changed = False
        for node_id, node in list(workflow.items()):
            if not isinstance(node, dict) or node.get("class_type") != "ComfySwitchNode":
                continue
            inputs = node.get("inputs") or {}
            flag = _resolve_constant_bool(workflow, inputs.get("switch"))
            if flag is None:
                continue
            selected = inputs.get("on_true" if flag else "on_false")
            for other in workflow.values():
                other_inputs = other.get("inputs") if isinstance(other, dict) else None
                if not isinstance(other_inputs, dict):
                    continue
                for field, value in other_inputs.items():
                    if isinstance(value, list) and len(value) == 2 \
                            and str(value[0]) == node_id:
                        other_inputs[field] = selected
            del workflow[node_id]
            changed = True


def prune_unreachable_nodes(workflow, object_info):
    """어떤 노드도 참조하지 않는 비출력(non-output) 노드를 반복 제거합니다.

    fold_constant_switches()로 끊어진 분기(예: 사용 안 하는 LoadImage 체인)를
    지워, ComfyUI 검증이 미사용 노드의 빈 입력 때문에 실패하지 않게 합니다.
    ComfyUI 자체도 출력 노드에서 역방향으로만 실행하므로 의미는 동일합니다.

    출력 노드 여부는 object_info의 output_node 플래그로 판정하고, object_info에
    정보가 없는 노드(미설치 커스텀 노드 등)는 안전하게 보존합니다.
    object_info가 None(ComfyUI 미연결)이면 아무것도 하지 않습니다.
    """
    if object_info is None:
        return
    while True:
        referenced = set()
        for node in workflow.values():
            inputs = node.get("inputs") if isinstance(node, dict) else None
            if not isinstance(inputs, dict):
                continue
            for value in inputs.values():
                if isinstance(value, list) and len(value) == 2:
                    referenced.add(str(value[0]))
        removable = []
        for node_id, node in workflow.items():
            if node_id in referenced or not isinstance(node, dict):
                continue
            info = object_info.get(node.get("class_type", ""))
            if not isinstance(info, dict) or info.get("output_node"):
                continue
            removable.append(node_id)
        if not removable:
            return
        for node_id in removable:
            del workflow[node_id]


def set_seed(workflow, seed):
    """모든 노드 inputs에서 seed/noise_seed 숫자 필드를 찾아 시드를 설정합니다."""
    found = False
    for node in workflow.values():
        inputs = node.get("inputs")
        if not isinstance(inputs, dict):
            continue
        for key in ("seed", "noise_seed"):
            if key in inputs and isinstance(inputs[key], (int, float)) and not isinstance(inputs[key], bool):
                inputs[key] = int(seed)
                found = True
    return found


def invalidate_object_info_cache():
    """/object_info 캐시를 비웁니다 (모델/노드를 추가한 뒤 즉시 반영이 필요할 때)."""
    global _OBJECT_INFO_CACHE
    with _OBJECT_INFO_LOCK:
        _OBJECT_INFO_CACHE = None


def fetch_object_info():
    """ComfyUI /object_info 전체를 조회합니다. 실패 시 None을 반환합니다.

    성공 응답은 OBJECT_INFO_TTL_SEC 동안 캐시합니다. 커스텀 노드가 많은 환경에서
    이 응답은 수 MB급이고 후보 생성 1회에 /workflows·/preflight가 각각 한 번씩
    조회하므로, 캐시가 없으면 생성마다 수 MB를 두 번 다시 받습니다.

    실패(None)는 캐시하지 않습니다 — ComfyUI 미기동 시 comfyReachable=false가
    TTL 동안 고착되면 ComfyUI를 켠 직후에도 계속 미연결로 보이기 때문입니다.

    반대 방향도 막아야 합니다. 캐시가 살아 있는 동안 ComfyUI가 내려가면 "성공 캐시"
    때문에 comfyReachable=true가 최대 TTL 동안 유지되어 기존 동작과 어긋납니다.
    그래서 캐시를 내주기 전에 /system_stats로 생존만 확인합니다(수 KB, localhost 수 ms).
    수 MB짜리 /object_info 재조회는 여전히 건너뛰므로 절감 효과는 그대로입니다.

    스레드 안전: 락은 캐시 읽기/쓰기 구간에만 걸고 HTTP 조회는 락 밖에서 합니다.
    캐시 미스가 동시에 발생하면 조회가 중복될 수 있지만(드묾), 락을 잡은 채 조회하면
    ComfyUI 미응답 시 최대 15초 동안 다른 모든 요청(/health·/job 폴링 포함)이 함께
    막히므로 중복 방지보다 응답성을 택했습니다. 중복 조회가 나도 결과는 동일하고
    마지막 성공값이 캐시에 남습니다.
    """
    global _OBJECT_INFO_CACHE

    now = time.monotonic()
    with _OBJECT_INFO_LOCK:
        cached = _OBJECT_INFO_CACHE
    if cached is not None and now - cached[0] < OBJECT_INFO_TTL_SEC:
        if comfy_alive():
            return cached[1]
        # ComfyUI가 내려갔다 — 캐시를 버리고 미연결(None)로 보고한다.
        invalidate_object_info_cache()
        return None

    try:
        status, body, _ = comfy_request("/object_info", timeout=15)
        if status != 200:
            return None
        parsed = json.loads(body.decode("utf-8"))
    except Exception:
        return None

    with _OBJECT_INFO_LOCK:
        _OBJECT_INFO_CACHE = (time.monotonic(), parsed)
    return parsed


def field_options_from_object_info(object_info, class_type, field):
    """object_info의 required/optional 입력 정의에서 해당 필드의 선택지 목록을 찾습니다.

    필드 정의의 첫 요소가 리스트면 그것이 선택지입니다. 신형 포맷
    ["COMBO", {"options": [...]}]도 지원합니다. 선택지(choice) 입력이 아니면
    None을 반환합니다. 빈 리스트는 "선택지가 0개"(해당 종류 파일 미설치)를 뜻하므로
    None과 구분해 그대로 반환합니다.
    """
    node_info = (object_info or {}).get(class_type)
    if not isinstance(node_info, dict):
        return None
    inputs = node_info.get("input") or {}
    for section in ("required", "optional"):
        section_map = inputs.get(section)
        if not isinstance(section_map, dict) or field not in section_map:
            continue
        spec = section_map[field]
        if not isinstance(spec, (list, tuple)) or not spec:
            continue
        raw = None
        if isinstance(spec[0], list):
            raw = spec[0]
        elif spec[0] == "COMBO" and len(spec) > 1 and isinstance(spec[1], dict) \
                and isinstance(spec[1].get("options"), list):
            raw = spec[1]["options"]
        if raw is not None:
            return [c for c in raw if isinstance(c, str)]
    return None


def attach_variable_options(variables, object_info, workflow):
    """string 변수에 ComfyUI object_info 기반 선택지(options)를 첨부한 사본을 반환합니다.

    ComfyUI 미기동/조회 실패(object_info=None), 워크플로 로드 실패(workflow=None),
    또는 선택지가 리스트가 아니면 options 키를 생략합니다.

    workflow는 호출 측이 이미 로드한 것을 받습니다 (같은 요청에서 두 번 로드하지 않기 위함).
    이 함수는 workflow를 읽기만 합니다.
    """
    result = [dict(v) for v in variables]
    string_vars = [v for v in result if v.get("type", "string") == "string"]
    if not string_vars or object_info is None or workflow is None:
        return result

    for var in string_vars:
        node = workflow.get(str(var.get("nodeId", "")))
        if not isinstance(node, dict):
            continue
        options = field_options_from_object_info(
            object_info, node.get("class_type", ""), var.get("field", ""))
        if options:
            var["options"] = options
    return result


# ──────────────────────────── 사전 검증 (preflight) ────────────────────────────

def build_workflow(workflow_name, variables, manifest, object_info=None):
    """워크플로를 로드하고 변수를 덮어쓴 최종 워크플로를 만듭니다.

    변수 적용 후 상수 스위치를 접고(fold) 끊어진 분기를 제거(prune)합니다.
    /generate 제출 경로와 /preflight 사전 검증이 이 함수를 공유합니다
    (치환 로직이 갈라지면 검증과 실제 제출 결과가 어긋나므로 복제 금지).
    반환: (workflow dict, 변수 적용 오류 목록)
    """
    workflow = load_workflow(workflow_name)
    errors = apply_variables(workflow, workflow_name, variables, manifest)
    if not errors:
        fold_constant_switches(workflow)
        prune_unreachable_nodes(workflow, object_info)
    return workflow, errors


def compute_missing_nodes(workflow, object_info):
    """워크플로의 class_type 집합 중 ComfyUI object_info 키에 없는 노드 목록을 반환합니다.

    ComfyUI 미연결(object_info=None)이면 검증 불가이므로 빈 목록을 반환합니다
    (호출 측이 comfyReachable로 구분).
    """
    if object_info is None:
        return []
    missing = set()
    for node in workflow.values():
        if not isinstance(node, dict):
            continue
        class_type = node.get("class_type")
        if class_type and class_type not in object_info:
            missing.add(class_type)
    return sorted(missing)


def validate_workflow_inputs(workflow, object_info):
    """최종 워크플로의 선택지(choice) 입력 값이 ComfyUI 설치 목록에 있는지 검증합니다.

    object_info에 선택지 목록이 있는 입력만 검사합니다. 즉 ckpt_name 같은 모델
    파일명뿐 아니라 LoadImage의 image 등 모든 choice 필드가 일반적으로 검증됩니다.
    - 노드 연결([nodeId, index])·숫자·불리언 값은 검증 대상이 아닙니다.
    - object_info에 없는 노드는 missingNodes에서 별도 보고하므로 건너뜁니다.
    반환: invalidInputs 목록 (node/classType/field/value/availableSample/availableCount).
    """
    invalid = []
    for node_id, node in workflow.items():
        if not isinstance(node, dict):
            continue
        class_type = node.get("class_type", "")
        if not class_type or class_type not in (object_info or {}):
            continue
        inputs = node.get("inputs")
        if not isinstance(inputs, dict):
            continue
        for field, value in inputs.items():
            if not isinstance(value, str):
                continue  # 노드 연결/숫자/불리언은 choice 값이 아님
            choices = field_options_from_object_info(object_info, class_type, field)
            if choices is None:
                continue  # 자유 입력 필드 (선택지 정의 없음)
            if value not in choices:
                invalid.append({
                    "node": node_id,
                    "classType": class_type,
                    "field": field,
                    "value": value,
                    "availableSample": choices[:10],
                    "availableCount": len(choices),
                })
    return invalid


# ──────────────────────────── ComfyUI 호출 ────────────────────────────

def comfy_request(path, data=None, headers=None, method=None, timeout=30):
    """ComfyUI에 HTTP 요청을 보내고 (status, body bytes)를 반환합니다."""
    url = COMFY_URL.rstrip("/") + path
    req = urllib.request.Request(url, data=data, method=method)
    for key, value in (headers or {}).items():
        req.add_header(key, value)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read(), resp.headers
    except urllib.error.HTTPError as e:
        return e.code, e.read(), e.headers


def comfy_alive():
    """ComfyUI /system_stats 응답 여부를 확인합니다."""
    try:
        status, _, _ = comfy_request("/system_stats", timeout=5)
        return status == 200
    except Exception:
        return False


def queue_prompt(workflow):
    """POST /prompt 로 워크플로를 큐잉하고 prompt_id를 반환합니다. 실패 시 예외."""
    body = json.dumps({"prompt": workflow, "client_id": CLIENT_ID}).encode("utf-8")
    status, resp, _ = comfy_request(
        "/prompt", data=body, headers={"Content-Type": "application/json"}, timeout=60)
    if status != 200:
        try:
            detail = resp.decode("utf-8", "replace")
        except Exception:
            detail = str(resp)
        raise RuntimeError(
            "ComfyUI가 워크플로를 거부했습니다 (HTTP %d). 모델/커스텀 노드 누락 여부를 확인하세요. 응답: %s"
            % (status, detail[:2000]))
    data = json.loads(resp.decode("utf-8"))
    prompt_id = data.get("prompt_id")
    if not prompt_id:
        raise RuntimeError("ComfyUI 응답에 prompt_id가 없습니다: %s" % data)
    return prompt_id


def collect_outputs(history_entry):
    """history 항목의 outputs에서 파일 정보 목록을 수집합니다 (output 타입 우선)."""
    files = []
    outputs = history_entry.get("outputs") or {}
    for node_output in outputs.values():
        if not isinstance(node_output, dict):
            continue
        for list_key in ("images", "audio", "gifs"):
            for item in node_output.get(list_key) or []:
                if isinstance(item, dict) and item.get("filename"):
                    files.append({
                        "filename": item.get("filename", ""),
                        "subfolder": item.get("subfolder", ""),
                        "type": item.get("type", "output"),
                    })
    saved = [f for f in files if f["type"] == "output"]
    return saved if saved else files


# ──────────────────────────── 생성 Job ────────────────────────────

def run_job(job_id, prompt_ids):
    """백그라운드 스레드: 큐잉된 prompt들의 완료를 /history로 폴링합니다.

    대기(time.sleep)는 루프 **끝**에서만 합니다. 첫 확인을 즉시 수행해야
    이미 끝난 prompt(캐시된 결과·초고속 워크플로)를 대기 없이 잡아내고,
    마지막 prompt가 끝난 직후에도 불필요한 sleep 없이 루프를 빠져나갑니다.
    """
    start = time.time()
    total = len(prompt_ids)
    pending = dict(prompt_ids)  # prompt_id -> seed
    results = []

    while pending:
        if time.time() - start > JOB_TIMEOUT_SEC:
            fail_job(job_id, "생성 대기가 제한 시간(%d초)을 초과했습니다. ComfyUI 부하/모델 로드 상태를 확인하세요."
                     % int(JOB_TIMEOUT_SEC))
            return

        for prompt_id in list(pending.keys()):
            try:
                status, body, _ = comfy_request("/history/" + urllib.parse.quote(prompt_id), timeout=15)
            except Exception as e:
                fail_job(job_id, "ComfyUI 연결이 끊겼습니다: %s" % e)
                return
            if status != 200:
                continue
            try:
                history = json.loads(body.decode("utf-8"))
            except Exception:
                continue
            entry = history.get(prompt_id)
            if not entry:
                continue

            status_info = entry.get("status") or {}
            if status_info.get("status_str") == "error":
                messages = status_info.get("messages") or []
                detail = json.dumps(messages, ensure_ascii=False)[:2000]
                fail_job(job_id, "ComfyUI 실행 오류 (모델/노드 누락 가능): %s" % detail)
                return

            outputs = collect_outputs(entry)
            if status_info.get("completed") or outputs:
                seed = pending.pop(prompt_id)
                for f in outputs:
                    entry_result = dict(f)
                    entry_result["seed"] = seed
                    results.append(entry_result)
                with JOBS_LOCK:
                    job = JOBS.get(job_id)
                    if job is not None:
                        job["progress"] = (total - len(pending)) / float(total)
                        job["results"] = list(results)
                        job["message"] = "%d/%d 완료" % (total - len(pending), total)

        # 아직 남은 prompt가 있을 때만 쉰다 (마지막 완료 직후의 불필요한 대기 제거).
        if pending:
            time.sleep(POLL_INTERVAL_SEC)

    with JOBS_LOCK:
        job = JOBS.get(job_id)
        if job is not None:
            job["status"] = "completed"
            job["progress"] = 1.0
            job["results"] = results
            job["message"] = "후보 %d개 생성 완료" % total


def fail_job(job_id, message):
    with JOBS_LOCK:
        job = JOBS.get(job_id)
        if job is not None:
            job["status"] = "failed"
            job["message"] = message


# ──────────────────────────── Host 헤더 검증 ────────────────────────────

def split_host_header(value):
    """Host 헤더를 (호스트명 소문자, 포트 문자열)로 나눕니다.

    `example.com`, `127.0.0.1:8189`, `[::1]:8189`, `[::1]`, `::1` 형태를 모두 다룹니다.
    포트가 없으면 포트는 빈 문자열입니다. 형식이 깨졌으면 (None, None)을 반환합니다.
    """
    text = (value or "").strip()
    if not text:
        return None, None

    if text.startswith("["):
        # IPv6 대괄호 표기: [::1] 또는 [::1]:8189
        end = text.find("]")
        if end < 0:
            return None, None
        host = text[1:end]
        rest = text[end + 1:]
        if not rest:
            port = ""
        elif rest.startswith(":"):
            port = rest[1:]
        else:
            return None, None
    elif text.count(":") == 1:
        host, _, port = text.partition(":")
    else:
        # 콜론이 없으면 포트 없는 호스트명, 2개 이상이면 대괄호 없는 IPv6 리터럴로 본다.
        host, port = text, ""

    return host.lower(), port


def is_loopback_host(host):
    """바인딩 주소가 루프백인지 판정합니다."""
    return (host or "").strip().lower().strip("[]") in ALLOWED_HOST_NAMES


def is_allowed_host_header(value):
    """Host 헤더가 이 서버를 가리키는 로컬 주소인지 판정합니다."""
    host, port = split_host_header(value)
    if host is None:
        return False
    if host not in ALLOWED_HOST_NAMES:
        return False
    # 포트 생략(기본 포트 표기)도 허용하고, 명시했다면 실제 서비스 포트와 일치해야 한다.
    return port == "" or port == str(BIND_PORT)


def wants_refresh(query):
    """쿼리 문자열에 refresh=1(또는 true/yes/on)이 있으면 True."""
    values = urllib.parse.parse_qs(query or "").get("refresh") or []
    return any(str(v).strip().lower() in ("1", "true", "yes", "on") for v in values)


# ──────────────────────────── HTTP 핸들러 ────────────────────────────

class BridgeHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # 콘솔 로그 간소화
        print("[bridge] %s - %s" % (self.address_string(), fmt % args))

    # ---- 응답 헬퍼 ----

    def send_json(self, obj, status=200):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def send_error_json(self, message, status=400):
        self.send_json({"ok": False, "error": message}, status)

    def read_body(self):
        length = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(length) if length > 0 else b""

    # ---- 요청 가드 ----

    def check_host_header(self):
        """Host 헤더가 허용 대상인지 확인하고, 아니면 403을 보낸 뒤 False를 반환합니다.

        브라우저가 보낸 교차 출처 요청(DNS rebinding·단순 요청 CSRF)을 걸러냅니다.
        Host 헤더가 아예 없는 요청(HTTP/1.0 클라이언트 등)은 브라우저발이 아니므로 통과시킵니다.
        """
        if not HOST_CHECK_ENABLED:
            return True

        value = self.headers.get("Host")
        if value is None:
            return True

        if is_allowed_host_header(value):
            return True

        self.send_error_json(
            "허용되지 않은 Host 헤더입니다: \"%s\". "
            "이 브리지 서버는 로컬 전용이라 http://127.0.0.1:%d 또는 http://localhost:%d 로만 "
            "호출할 수 있습니다(브라우저를 통한 외부 페이지의 호출을 막기 위한 검증입니다). "
            "Unity의 Tools/AI Asset Pipeline/Settings에서 \"브리지 서버 주소\"를 http://127.0.0.1:%d 형식으로 "
            "맞춰주세요. 다른 기기에서 접속해야 한다면 서버를 --host <주소> 로 실행하세요"
            "(그 경우 Host 검증은 비활성화됩니다)."
            % (value, BIND_PORT, BIND_PORT, BIND_PORT), 403)
        return False

    def check_body_length(self, limit):
        """Content-Length를 검사해 본문을 읽어도 되는지 확인합니다.

        상한 초과·헤더 누락·숫자가 아닌 값이면 400을 보낸 뒤 None을 반환합니다.
        (헤더가 없으면 얼마나 읽어야 할지 알 수 없으므로 거부합니다.)
        """
        raw = self.headers.get("Content-Length")
        if raw is None:
            self.send_error_json(
                "Content-Length 헤더가 필요합니다. 요청 본문 길이를 명시해 다시 호출해주세요.")
            return None
        try:
            length = int(str(raw).strip())
        except (TypeError, ValueError):
            self.send_error_json("Content-Length 헤더가 올바른 숫자가 아닙니다: \"%s\"." % raw)
            return None
        if length < 0:
            self.send_error_json("Content-Length 헤더가 음수입니다: %d." % length)
            return None
        if length > limit:
            self.send_error_json(
                "요청 본문이 너무 큽니다 (%d바이트, 상한 %d바이트). "
                "변수 값이나 업로드 파일 크기를 줄여 다시 시도해주세요." % (length, limit))
            return None
        return length

    # ---- GET ----

    def do_GET(self):
        if not self.check_host_header():
            return
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        try:
            if path == "/health":
                # scriptPath/version은 "지금 이 포트에 떠 있는 브리지가 누구인지"를 알려준다.
                # 여러 Unity 프로젝트가 같은 포트를 공유하면 다른 프로젝트의 워크플로·변수를
                # 쓰게 되므로, Unity가 자기 설치 경로와 대조해 경고할 수 있도록 노출한다.
                # (기존 키는 파싱 호환을 위해 그대로 유지)
                self.send_json({"ok": True, "comfyUrl": COMFY_URL, "comfyAlive": comfy_alive(),
                                "userDirActive": user_dir_active(), "jobTimeoutSec": JOB_TIMEOUT_SEC,
                                "scriptPath": SCRIPT_PATH, "version": BRIDGE_VERSION})
            elif path == "/workflows":
                # ?refresh=1: 캐시를 비우고 다시 조회한다. ComfyUI에 모델·커스텀 노드를 추가한
                # 직후 TTL을 기다리지 않고 목록을 갱신하는 수동 무효화 경로다.
                if wants_refresh(parsed.query):
                    invalidate_object_info_cache()
                    invalidate_workflow_cache()
                manifest = load_variables_manifest()
                # ComfyUI가 살아 있으면 설치된 모델/샘플러 선택지를 변수에 첨부한다 (1회 조회).
                object_info = fetch_object_info()
                workflows = []
                for name in list_workflow_names():
                    # 워크플로 JSON은 항목당 1회만 로드해 옵션 첨부와 누락 노드 검증이 함께 쓴다
                    # (두 경로 모두 읽기 전용). ComfyUI 미연결이면 둘 다 검증을 건너뛰므로
                    # 로드 자체를 하지 않는다. 로드 실패는 목록 응답을 깨지 않고 무시한다
                    # (기존과 동일한 안전 폴백: options 없음 + missingNodes 빈 목록).
                    workflow = None
                    if object_info is not None:
                        try:
                            workflow = load_workflow(name)
                        except Exception:
                            workflow = None
                    variables = attach_variable_options(
                        manifest.get(name, []), object_info, workflow)
                    missing_nodes = (
                        compute_missing_nodes(workflow, object_info) if workflow is not None else [])
                    workflows.append({
                        "name": name,
                        "variables": variables,
                        "missingNodes": missing_nodes,
                    })
                self.send_json({
                    "ok": True,
                    "comfyReachable": object_info is not None,
                    "workflows": workflows,
                })
            elif path.startswith("/job/"):
                job_id = path[len("/job/"):]
                with JOBS_LOCK:
                    job = JOBS.get(job_id)
                    job_copy = dict(job) if job else None
                if job_copy is None:
                    self.send_error_json("job을 찾을 수 없습니다: %s" % job_id, 404)
                else:
                    job_copy["ok"] = True
                    self.send_json(job_copy)
            elif path == "/view":
                self.proxy_view(parsed.query)
            else:
                self.send_error_json("알 수 없는 경로: %s" % path, 404)
        except Exception as e:
            self.send_error_json("서버 오류: %s" % e, 500)

    def proxy_view(self, query):
        """ComfyUI /view 를 그대로 프록시합니다 (결과 파일 다운로드)."""
        try:
            status, body, headers = comfy_request("/view?" + query, timeout=120)
        except Exception as e:
            self.send_error_json("ComfyUI에 연결할 수 없습니다: %s" % e, 502)
            return
        self.send_response(status)
        self.send_header("Content-Type", headers.get("Content-Type", "application/octet-stream"))
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    # ---- POST ----

    def do_POST(self):
        if not self.check_host_header():
            return
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        # 본문을 읽기 전에 길이를 먼저 검사한다. /upload만 이미지 원본을 싣기 때문에 상한이 크다.
        limit = MAX_UPLOAD_BODY_BYTES if path == "/upload" else MAX_BODY_BYTES
        if self.check_body_length(limit) is None:
            return
        try:
            if path == "/generate":
                self.handle_generate()
            elif path == "/preflight":
                self.handle_preflight()
            elif path == "/upload":
                self.handle_upload()
            elif path == "/free":
                self.handle_free()
            elif path == "/shutdown":
                self.handle_shutdown()
            else:
                self.send_error_json("알 수 없는 경로: %s" % path, 404)
        except Exception as e:
            self.send_error_json("서버 오류: %s" % e, 500)

    def handle_generate(self):
        try:
            req = json.loads(self.read_body().decode("utf-8"))
        except Exception:
            self.send_error_json("요청 본문이 올바른 JSON이 아닙니다.")
            return

        workflow_name = req.get("workflow")
        if not workflow_name:
            self.send_error_json("workflow 파라미터가 필요합니다.")
            return
        variables = req.get("variables") or {}

        # count: 누락/0/None이면 기본값 4로 두되(기존 동작 유지), 상한 초과는 조용히 잘라내지 않고
        # 400으로 거절한다. 오타 하나로 수천 건이 큐잉되는 것을 사용자가 알아야 하기 때문이다.
        raw_count = req.get("count")
        try:
            count = int(raw_count or 4)
        except (TypeError, ValueError):
            self.send_error_json("count는 정수여야 합니다 (요청값: %r)." % (raw_count,))
            return
        if count < 1 or count > MAX_GENERATE_COUNT:
            self.send_error_json(
                "count는 1~%d 사이여야 합니다 (요청값: %d)." % (MAX_GENERATE_COUNT, count))
            return

        base_seed = req.get("baseSeed")
        if base_seed is None:
            base_seed = random.randint(0, 9_000_000_000_000_000)
        try:
            base_seed = int(base_seed)
        except (TypeError, ValueError):
            self.send_error_json("baseSeed는 정수여야 합니다 (요청값: %r)." % (req.get("baseSeed"),))
            return

        if not comfy_alive():
            self.send_error_json(
                "ComfyUI 서버(%s)가 응답하지 않습니다. ComfyUI를 먼저 실행해주세요." % COMFY_URL, 502)
            return

        seed_warning = ""
        try:
            manifest = load_variables_manifest()
            object_info = fetch_object_info()  # 미사용 분기 제거(prune)용 — None이면 제거 생략
            prompt_ids = {}
            for i in range(count):
                workflow, errors = build_workflow(workflow_name, variables, manifest, object_info)
                if errors:
                    self.send_error_json("변수 적용 실패: " + "; ".join(errors))
                    return
                seed = base_seed + i
                if not set_seed(workflow, seed) and not seed_warning:
                    # 시드 필드가 없으면 동일한 프롬프트가 큐잉되어 ComfyUI가 중복 제거로
                    # 결과를 1개만 낼 수 있다. 경고를 job 상태에 남겨 사용자에게 알린다.
                    seed_warning = (
                        "워크플로 '%s'에서 seed/noise_seed 입력을 찾지 못했습니다. "
                        "동일한 프롬프트가 중복 제거되어 후보가 1개만 생성될 수 있습니다. "
                        "워크플로 JSON에 seed 필드를 추가해주세요." % workflow_name)
                    print("[bridge] 경고: " + seed_warning)
                prompt_ids[queue_prompt(workflow)] = seed
        except FileNotFoundError as e:
            self.send_error_json(str(e), 404)
            return
        except RuntimeError as e:
            self.send_error_json(str(e), 502)
            return

        job_id = uuid.uuid4().hex
        with JOBS_LOCK:
            JOBS[job_id] = {
                "status": "running",
                "progress": 0.0,
                "message": "0/%d 완료" % count,
                "results": [],
                "workflow": workflow_name,
                "baseSeed": base_seed,
                "count": count,
                "warning": seed_warning,
            }
        threading.Thread(target=run_job, args=(job_id, prompt_ids), daemon=True).start()
        self.send_json({"ok": True, "jobId": job_id, "baseSeed": base_seed, "count": count})

    def handle_preflight(self):
        """생성 제출 전 사전 검증: 커스텀 노드 존재 + choice 입력 값 유효성.

        요청 바디 {workflow, variables}를 받아 /generate와 동일한 치환 로직
        (build_workflow)으로 최종 워크플로를 만든 뒤, /object_info와 대조합니다.
        ComfyUI 미연결이면 검증을 건너뛰고 comfyReachable=false로 응답합니다
        (생성 경로가 연결 오류를 직접 안내하므로 여기서는 죽지 않습니다).
        """
        try:
            req = json.loads(self.read_body().decode("utf-8"))
        except Exception:
            self.send_error_json("요청 본문이 올바른 JSON이 아닙니다.")
            return

        workflow_name = req.get("workflow")
        if not workflow_name:
            self.send_error_json("workflow 파라미터가 필요합니다.")
            return
        variables = req.get("variables") or {}

        object_info = fetch_object_info()
        try:
            manifest = load_variables_manifest()
            workflow, errors = build_workflow(workflow_name, variables, manifest, object_info)
        except FileNotFoundError as e:
            self.send_error_json(str(e), 404)
            return
        if errors:
            self.send_error_json("변수 적용 실패: " + "; ".join(errors))
            return

        if object_info is None:
            self.send_json({
                "ok": True,
                "comfyReachable": False,
                "missingNodes": [],
                "invalidInputs": [],
            })
            return

        self.send_json({
            "ok": True,
            "comfyReachable": True,
            "missingNodes": compute_missing_nodes(workflow, object_info),
            "invalidInputs": validate_workflow_inputs(workflow, object_info),
        })

    def handle_free(self):
        """ComfyUI POST /free 를 프록시해 로드된 모델을 언로드하고 메모리를 해제합니다."""
        # 언로드 자체로 설치 목록이 바뀌지는 않지만, /free는 "ComfyUI 상태를 손댔다"는 신호이므로
        # 이 시점에 캐시를 비워 다음 조회가 최신 목록을 받게 한다 (수동 새로고침 수단 겸용).
        invalidate_object_info_cache()
        body = json.dumps({"unload_models": True, "free_memory": True}).encode("utf-8")
        try:
            status, resp, _ = comfy_request(
                "/free", data=body, headers={"Content-Type": "application/json"}, timeout=60)
        except Exception as e:
            self.send_error_json("ComfyUI에 연결할 수 없습니다: %s" % e, 502)
            return
        if status != 200:
            self.send_error_json(
                "ComfyUI 모델 언로드 실패 (HTTP %d): %s"
                % (status, resp.decode("utf-8", "replace")[:500]), 502)
            return
        self.send_json({"ok": True})

    def handle_upload(self):
        """멀티파트 본문을 ComfyUI /upload/image 로 그대로 전달합니다."""
        content_type = self.headers.get("Content-Type") or ""
        if "multipart/form-data" not in content_type:
            self.send_error_json("multipart/form-data 요청이 필요합니다.")
            return
        body = self.read_body()
        try:
            status, resp, _ = comfy_request(
                "/upload/image", data=body, headers={"Content-Type": content_type}, timeout=120)
        except Exception as e:
            self.send_error_json("ComfyUI에 연결할 수 없습니다: %s" % e, 502)
            return
        if status != 200:
            self.send_error_json(
                "ComfyUI 업로드 실패 (HTTP %d): %s" % (status, resp.decode("utf-8", "replace")[:500]), 502)
            return
        try:
            data = json.loads(resp.decode("utf-8"))
        except Exception:
            data = {}
        self.send_json({"ok": True, "name": data.get("name", ""), "subfolder": data.get("subfolder", ""),
                        "type": data.get("type", "input")})


    def handle_shutdown(self):
        """서버를 정상 종료합니다.

        브리지를 시작한 Unity 세션이 아니면 PID를 모르기 때문에(SessionState는 에디터
        재시작 시 사라진다) 프로세스를 죽일 수 없습니다. 그 경우 이 엔드포인트로
        서버가 스스로 내려가게 합니다. 서버는 127.0.0.1에만 바인딩되므로 로컬에서만
        호출할 수 있습니다.

        serve_forever()를 돌리는 스레드에서 shutdown()을 호출하면 교착되므로,
        응답을 먼저 보내고 별도 스레드에서 종료합니다.
        """
        # 로컬 외 주소에 바인딩한 경우(--host 지정)는 같은 네트워크의 다른 기기도
        # 이 엔드포인트를 부를 수 있으므로 거부한다. 그 경우 서버를 띄운 콘솔에서 끈다.
        if BIND_HOST not in ("127.0.0.1", "localhost", "::1"):
            self.send_error_json(
                "로컬 외 주소(%s)에 바인딩된 서버는 원격 종료를 지원하지 않습니다. "
                "서버를 실행한 콘솔 창에서 종료해주세요." % BIND_HOST, 403)
            return

        self.send_json({"ok": True, "message": "브리지 서버를 종료합니다."})
        print("[bridge] /shutdown 요청을 받아 서버를 종료합니다.")
        threading.Thread(target=self.server.shutdown, daemon=True).start()


class BridgeHTTPServer(ThreadingHTTPServer):
    """포트 중복 바인딩을 반드시 실패시키는 HTTP 서버.

    http.server의 기본값은 allow_reuse_address = 1(SO_REUSEADDR)인데, Windows에서
    SO_REUSEADDR는 "이미 리슨 중인 포트에도 바인딩을 허용"하는 의미라 두 번째 브리지가
    죽지 않고 떠버립니다. 그러면 Unity의 조기 종료 감지가 발동하지 않고 요청이 두 서버로
    비결정적으로 나뉩니다. 명시적으로 꺼서 중복 바인딩이 OSError로 실패하게 합니다.
    """

    allow_reuse_address = False


def create_server(host, port):
    """브리지 HTTP 서버를 만듭니다. 포트가 이미 사용 중이면 안내 후 종료(exit 1)합니다."""
    try:
        return BridgeHTTPServer((host, port), BridgeHandler)
    except OSError as e:
        sys.stderr.write(
            "[bridge] 포트 %d에 바인딩하지 못했습니다: %s\n" % (port, e)
        )
        sys.stderr.write(
            "[bridge] 이미 다른 브리지 서버나 프로그램이 이 포트를 쓰고 있습니다.\n"
        )
        sys.stderr.write(
            "[bridge] 다음을 순서대로 확인해주세요. "
            "① 다른 Unity 프로젝트에서 실행한 브리지 서버가 있으면 그 프로젝트의 3단계 창에서 "
            "[서버 종료]를 누르거나 콘솔 창을 닫아주세요. "
            "② 계속 이 프로젝트에서 별도로 띄우려면 Unity의 Tools/AI Asset Pipeline/Settings에서 "
            "\"브리지 서버 주소\"의 포트를 다른 번호(예: 8190)로 바꿔주세요. "
            "③ 브리지가 아닌 다른 프로그램이 포트를 쓰고 있다면 그 프로세스를 종료해주세요 "
            "(Windows: netstat -ano | findstr :%d 로 PID 확인).\n" % port
        )
        sys.exit(1)


def main():
    global COMFY_URL, USER_DIR, JOB_TIMEOUT_SEC, BIND_HOST, BIND_PORT, HOST_CHECK_ENABLED
    parser = argparse.ArgumentParser(description="AIAssetPipeline ComfyUI 브리지 서버")
    parser.add_argument("--host", default="127.0.0.1",
                        help="바인딩할 주소. 기본값 127.0.0.1(로컬 전용). "
                             "로컬 외 주소를 지정하면 같은 네트워크의 다른 기기가 접속할 수 있습니다.")
    parser.add_argument("--port", type=int, default=8189)
    parser.add_argument("--comfy-url", default="http://127.0.0.1:8188")
    parser.add_argument("--log-file", default="",
                        help="지정하면 stdout/stderr를 이 파일에 기록합니다 (콘솔 창 없이 실행할 때 사용).")
    parser.add_argument("--user-dir", default="",
                        help="사용자 오버라이드 폴더 절대 경로. 이 폴더의 workflows/*.json과 "
                             "variables.json이 패키지 동봉본보다 우선합니다.")
    parser.add_argument("--job-timeout", type=float, default=600.0,
                        help="생성 Job 1건의 최대 대기 시간(초).")
    args = parser.parse_args()
    COMFY_URL = args.comfy_url.rstrip("/")
    USER_DIR = os.path.normpath(args.user_dir) if args.user_dir else ""
    JOB_TIMEOUT_SEC = args.job_timeout

    if args.log_file:
        # 콘솔 창 없이(hidden) 실행될 때 로그를 파일로 남긴다. line buffering으로 즉시 기록.
        log_stream = open(args.log_file, "a", encoding="utf-8", buffering=1)
        sys.stdout = log_stream
        sys.stderr = log_stream

    host = (args.host or "127.0.0.1").strip() or "127.0.0.1"
    BIND_HOST = host
    BIND_PORT = args.port
    # 외부 바인딩이면 접속에 쓰일 호스트명을 알 수 없어 허용 목록을 만들 수 없으므로 검증을 끈다.
    # (자세한 정책 근거는 HOST_CHECK_ENABLED 선언부 주석 참고)
    HOST_CHECK_ENABLED = is_loopback_host(host)

    server = create_server(host, args.port)
    print("[bridge] AIAssetPipeline 브리지 서버 시작: http://%s:%d (ComfyUI: %s, 버전 %s)"
          % (host, args.port, COMFY_URL, BRIDGE_VERSION))
    print("[bridge] 스크립트 경로: %s" % SCRIPT_PATH)
    if not HOST_CHECK_ENABLED:
        print("[bridge] 경고: 로컬 외 주소(%s)에 바인딩합니다 — "
              "같은 네트워크의 다른 기기가 접속할 수 있습니다. "
              "의도한 것이 아니면 Tools/AI Asset Pipeline/Settings의 브리지 서버 주소를 "
              "http://127.0.0.1:<포트> 로 되돌려주세요." % host)
        print("[bridge] 경고: 외부 바인딩이므로 Host 검증이 비활성화됩니다. "
              "신뢰할 수 있는 네트워크에서만 사용하세요.")
    if user_dir_active():
        print("[bridge] 사용자 오버라이드 폴더 사용: %s" % USER_DIR)
    elif USER_DIR:
        print("[bridge] 사용자 오버라이드 폴더가 없어 패키지 동봉본을 사용합니다: %s" % USER_DIR)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
