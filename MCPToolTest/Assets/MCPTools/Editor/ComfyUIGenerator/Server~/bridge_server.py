# -*- coding: utf-8 -*-
"""MCPTools 브리지 서버.

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
        "'Add python.exe to PATH' 체크), Unity의 Tools/MCP/Settings에서 "
        "[Python 자동 탐지]를 누르거나 python.exe 절대 경로를 지정해주세요.\n"
    )
    sys.exit(1)

import argparse
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
BRIDGE_VERSION = "0.1.0"

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

# jobId -> job dict (락으로 보호)
JOBS = {}
JOBS_LOCK = threading.Lock()

POLL_INTERVAL_SEC = 1.0
JOB_TIMEOUT_SEC = 600.0

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


def load_workflow(name):
    """원본 워크플로 JSON을 그대로 로드합니다 (구조 변경 없음).

    "<user-dir>/workflows/<이름>.json"이 있으면 그것을 우선 로드하고,
    없으면 패키지 동봉 폴더에서 로드합니다.
    파일이 보이지 않거나 열리지 않을 때, 경로 길이가 임계값을 넘으면
    "파일 없음"과 구분되도록 경로 길이 안내를 메시지에 덧붙입니다
    (Windows는 MAX_PATH를 넘는 경로를 오류 대신 "없음"으로 돌려주기도 합니다).
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
    return read_json_file(path)


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


def fetch_object_info():
    """ComfyUI /object_info 전체를 조회합니다. 실패 시 None을 반환합니다."""
    try:
        status, body, _ = comfy_request("/object_info", timeout=15)
        if status != 200:
            return None
        return json.loads(body.decode("utf-8"))
    except Exception:
        return None


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


def attach_variable_options(name, variables, object_info):
    """string 변수에 ComfyUI object_info 기반 선택지(options)를 첨부한 사본을 반환합니다.

    ComfyUI 미기동/조회 실패(object_info=None) 또는 선택지가 리스트가 아니면
    options 키를 생략합니다.
    """
    result = [dict(v) for v in variables]
    string_vars = [v for v in result if v.get("type", "string") == "string"]
    if not string_vars or object_info is None:
        return result

    try:
        workflow = load_workflow(name)
    except Exception:
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

def build_workflow(workflow_name, variables, manifest):
    """워크플로를 로드하고 변수를 덮어쓴 최종 워크플로를 만듭니다.

    /generate 제출 경로와 /preflight 사전 검증이 이 함수를 공유합니다
    (치환 로직이 갈라지면 검증과 실제 제출 결과가 어긋나므로 복제 금지).
    반환: (workflow dict, 변수 적용 오류 목록)
    """
    workflow = load_workflow(workflow_name)
    errors = apply_variables(workflow, workflow_name, variables, manifest)
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
    """백그라운드 스레드: 큐잉된 prompt들의 완료를 /history로 폴링합니다."""
    start = time.time()
    total = len(prompt_ids)
    pending = dict(prompt_ids)  # prompt_id -> seed
    results = []

    while pending:
        if time.time() - start > JOB_TIMEOUT_SEC:
            fail_job(job_id, "생성 대기가 제한 시간(%d초)을 초과했습니다. ComfyUI 부하/모델 로드 상태를 확인하세요."
                     % int(JOB_TIMEOUT_SEC))
            return
        time.sleep(POLL_INTERVAL_SEC)

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

    # ---- GET ----

    def do_GET(self):
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
                manifest = load_variables_manifest()
                # ComfyUI가 살아 있으면 설치된 모델/샘플러 선택지를 변수에 첨부한다 (1회 조회).
                object_info = fetch_object_info()
                workflows = []
                for name in list_workflow_names():
                    variables = attach_variable_options(name, manifest.get(name, []), object_info)
                    # ComfyUI 연결 시에만 누락 커스텀 노드를 검증한다. 실패해도 목록
                    # 응답 자체는 유지한다 (옵션 첨부와 동일한 안전 폴백).
                    missing_nodes = []
                    if object_info is not None:
                        try:
                            missing_nodes = compute_missing_nodes(load_workflow(name), object_info)
                        except Exception:
                            missing_nodes = []
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
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        try:
            if path == "/generate":
                self.handle_generate()
            elif path == "/preflight":
                self.handle_preflight()
            elif path == "/upload":
                self.handle_upload()
            elif path == "/free":
                self.handle_free()
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
        count = max(1, int(req.get("count") or 4))
        base_seed = req.get("baseSeed")
        if base_seed is None:
            base_seed = random.randint(0, 9_000_000_000_000_000)
        base_seed = int(base_seed)

        if not comfy_alive():
            self.send_error_json(
                "ComfyUI 서버(%s)가 응답하지 않습니다. ComfyUI를 먼저 실행해주세요." % COMFY_URL, 502)
            return

        seed_warning = ""
        try:
            manifest = load_variables_manifest()
            prompt_ids = {}
            for i in range(count):
                workflow, errors = build_workflow(workflow_name, variables, manifest)
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

        try:
            manifest = load_variables_manifest()
            workflow, errors = build_workflow(workflow_name, variables, manifest)
        except FileNotFoundError as e:
            self.send_error_json(str(e), 404)
            return
        if errors:
            self.send_error_json("변수 적용 실패: " + "; ".join(errors))
            return

        object_info = fetch_object_info()
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
            "② 계속 이 프로젝트에서 별도로 띄우려면 Unity의 Tools/MCP/Settings에서 "
            "\"브리지 서버 주소\"의 포트를 다른 번호(예: 8190)로 바꿔주세요. "
            "③ 브리지가 아닌 다른 프로그램이 포트를 쓰고 있다면 그 프로세스를 종료해주세요 "
            "(Windows: netstat -ano | findstr :%d 로 PID 확인).\n" % port
        )
        sys.exit(1)


def main():
    global COMFY_URL, USER_DIR, JOB_TIMEOUT_SEC
    parser = argparse.ArgumentParser(description="MCPTools ComfyUI 브리지 서버")
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

    server = create_server(host, args.port)
    print("[bridge] MCPTools 브리지 서버 시작: http://%s:%d (ComfyUI: %s, 버전 %s)"
          % (host, args.port, COMFY_URL, BRIDGE_VERSION))
    print("[bridge] 스크립트 경로: %s" % SCRIPT_PATH)
    if host not in ("127.0.0.1", "localhost", "::1"):
        print("[bridge] 경고: 로컬 외 주소(%s)에 바인딩합니다 — "
              "같은 네트워크의 다른 기기가 접속할 수 있습니다. "
              "의도한 것이 아니면 Tools/MCP/Settings의 브리지 서버 주소를 "
              "http://127.0.0.1:<포트> 로 되돌려주세요." % host)
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
