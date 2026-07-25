using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// 슬라이스된 시트에서 찾은 동작(행) 1개입니다. 클립 1개에 대응합니다.
    /// (프레임은 스프라이트 이름의 마지막 번호 오름차순으로 정렬되어 있습니다)
    /// </summary>
    public sealed class SpriteSheetActionGroup
    {
        /// <summary>동작명입니다 (스프라이트 이름 <c>{동작}_{번호}</c>의 앞부분).</summary>
        public string action = string.Empty;

        /// <summary>이 동작의 프레임 수입니다.</summary>
        public int frameCount;

        /// <summary>만들어질(또는 갱신될) 클립 경로입니다 (Assets/ 기준 상대 경로).</summary>
        public string clipPath = string.Empty;

        /// <summary>같은 경로에 이미 클립이 있으면 true입니다. (있으면 새로 만들지 않고 커브만 덮어씁니다)</summary>
        public bool clipExists;

        /// <summary>루프 재생 여부입니다. 기본값은 동작명 규칙(idle/walk/run ON)이며 호출자가 바꿀 수 있습니다.</summary>
        public bool loop;

        /// <summary>클립 생성 대상에 포함할지 여부입니다. false면 이 동작은 건너뜁니다.</summary>
        public bool include = true;

        /// <summary>이번 실행에서 클립을 새로 만들었으면 true, 기존 클립을 갱신했으면 false입니다. (결과용)</summary>
        public bool created;

        /// <summary>프레임 순서대로 정렬된 스프라이트 목록입니다.</summary>
        internal List<Sprite> Sprites = new List<Sprite>();
    }

    /// <summary>
    /// 슬라이스된 시트를 훑어 만든 클립 생성 계획입니다.
    /// 동작 목록과 건너뛴 스프라이트 사유를 담으며, 실행 전에 덮어쓸 클립 목록을 확인하는 용도로도 씁니다.
    /// </summary>
    public sealed class SpriteSheetClipPlan
    {
        /// <summary>대상 시트 텍스처 경로 (Assets/ 기준 상대 경로).</summary>
        public string sheetPath = string.Empty;

        /// <summary>시트 이름(확장자 제외)입니다. 클립 출력 폴더 이름으로 사용됩니다.</summary>
        public string sheetName = string.Empty;

        /// <summary>클립이 만들어질 폴더입니다 (<c>Animations/{시트이름}</c>).</summary>
        public string outputFolder = string.Empty;

        /// <summary>동작(=클립) 목록입니다.</summary>
        public List<SpriteSheetActionGroup> groups = new List<SpriteSheetActionGroup>();

        /// <summary>이름 규칙에 맞지 않아 건너뛴 항목과 사유입니다.</summary>
        public List<string> skipped = new List<string>();

        /// <summary>
        /// 포함(<see cref="SpriteSheetActionGroup.include"/>)된 동작 중 이미 클립이 있어
        /// 덮어쓰게 될 클립 경로 목록입니다. (실행 전 확인 다이얼로그용)
        /// </summary>
        /// <returns>덮어쓸 클립 경로 목록. 없으면 빈 목록.</returns>
        public List<string> ExistingClipPaths()
        {
            var paths = new List<string>();
            foreach (SpriteSheetActionGroup group in groups)
            {
                if (group.include && group.clipExists)
                {
                    paths.Add(group.clipPath);
                }
            }

            return paths;
        }
    }

    /// <summary>스프라이트 시트 클립 생성 결과입니다.</summary>
    public sealed class SpriteSheetClipBuildResult
    {
        /// <summary>대상 시트 텍스처 경로 (Assets/ 기준 상대 경로).</summary>
        public string sheetPath = string.Empty;

        /// <summary>실제로 만들거나 갱신한 동작 목록입니다 (<c>created</c>로 신규 여부 구분).</summary>
        public List<SpriteSheetActionGroup> groups = new List<SpriteSheetActionGroup>();

        /// <summary>건너뛴 항목과 사유입니다 (이름 규칙 불일치, 제외한 동작, 프리팹 연결 실패 등).</summary>
        public List<string> skipped = new List<string>();

        /// <summary>생성·갱신한 AnimatorController 경로입니다. 만들지 않았으면 빈 문자열.</summary>
        public string controllerPath = string.Empty;

        /// <summary>컨트롤러에 이번에 새로 추가한 State 이름 목록입니다.</summary>
        public List<string> addedStates = new List<string>();

        /// <summary>Animator를 붙이고 컨트롤러를 할당한 프리팹 경로입니다. 연결하지 않았으면 빈 문자열.</summary>
        public string prefabPath = string.Empty;

        /// <summary>클립 커브가 가리키는 대상 오브젝트 경로입니다 (빈 문자열이면 Animator가 붙은 오브젝트 자신).</summary>
        public string objectPath = string.Empty;

        /// <summary>프리팹에 Animator + 컨트롤러 연결까지 마쳤으면 true입니다.</summary>
        public bool prefabLinked;
    }

    /// <summary>
    /// 슬라이스가 끝난 스프라이트 시트(Sprite Mode=Multiple)의 서브 스프라이트를 동작별로 묶어
    /// AnimationClip을 만들고, 선택적으로 AnimatorController 구성 + 대상 프리팹 연결까지 수행합니다.
    /// <para>
    /// 스프라이트 이름은 <c>{동작}_{번호}</c> 규칙(마지막 <c>_</c> 뒤의 숫자가 프레임 번호)으로 파싱하므로
    /// 동작명에 <c>_</c>가 들어가도(<c>attack_combo_01</c>) 안전합니다. 규칙에 맞지 않는 이름은 건너뛰고
    /// 사유를 결과에 남깁니다.
    /// </para>
    /// <para>
    /// 격자 검출·배경 제거·슬라이스 로직(<see cref="SpriteSheetImporter"/>)에는 관여하지 않으며,
    /// 이미 슬라이스된 결과만 입력으로 사용합니다.
    /// </para>
    /// </summary>
    public static class SpriteSheetClipBuilder
    {
        /// <summary>대상 컴포넌트 지정값: 2D 스프라이트 렌더러입니다.</summary>
        public const string SpriteRendererComponent = "SpriteRenderer";

        /// <summary>대상 컴포넌트 지정값: uGUI Image입니다.</summary>
        public const string ImageComponent = "Image";

        /// <summary>스프라이트 이름 파싱 규칙입니다 (마지막 <c>_</c> 뒤 숫자가 프레임 번호).</summary>
        private static readonly Regex FrameNamePattern = new Regex(@"^(?<action>.+)_(?<no>\d+)$");

        /// <summary>기본 루프 ON 대상 동작 접두사입니다 (반복 이동/대기 계열).</summary>
        private static readonly string[] LoopingActionPrefixes = { "idle", "walk", "run" };

        /// <summary>스프라이트 렌더러/Image 모두 스프라이트를 담는 직렬화 필드 이름입니다.</summary>
        private const string SpritePropertyName = "m_Sprite";

        /// <summary>
        /// 슬라이스된 시트의 서브 스프라이트를 동작별로 묶어 클립 생성 계획을 만듭니다.
        /// 프로젝트에는 아무것도 기록하지 않으므로, 계획의 동작명·루프·포함 여부를 확인·수정한 뒤
        /// <see cref="Build"/>에 넘기면 됩니다.
        /// </summary>
        /// <param name="sheetPath">슬라이스된 시트 텍스처 경로 (Assets/ 기준 상대 경로).</param>
        /// <returns>동작 목록과 건너뛴 사유가 담긴 계획.</returns>
        /// <exception cref="InvalidOperationException">
        /// 경로가 비었거나 텍스처를 찾을 수 없거나, 서브 스프라이트가 하나도 없을 때.
        /// 메시지에 원인과 조치를 포함합니다.
        /// </exception>
        /// <summary>
        /// 시트 경로에 대응하는 AnimatorController 경로를 규칙대로 만듭니다
        /// (<c>{확정본}/Animations/{시트이름}/{시트이름}.controller</c>). 파일 존재 여부는 확인하지 않습니다.
        /// 한 시트에서 만든 클립·컨트롤러는 항상 이 한 곳에 모이므로, 시트만 알면 컨트롤러를 찾을 수 있습니다.
        /// </summary>
        /// <param name="sheetPath">시트 텍스처 경로 (Assets/ 기준 상대 경로).</param>
        /// <returns>컨트롤러 경로. sheetPath가 비어 있으면 빈 문자열.</returns>
        public static string ControllerPathForSheet(string sheetPath)
        {
            if (string.IsNullOrWhiteSpace(sheetPath))
            {
                return string.Empty;
            }

            string sheetName = Path.GetFileNameWithoutExtension(sheetPath.Trim().Replace('\\', '/'));
            string folder = $"{MCPToolFolders.AnimationsDir(MCPToolSettings.GetOrCreate())}/{sheetName}";
            return $"{folder}/{sheetName}.controller";
        }

        public static SpriteSheetClipPlan Scan(string sheetPath)
        {
            if (string.IsNullOrWhiteSpace(sheetPath))
            {
                throw new InvalidOperationException(
                    "시트 경로(sheetPath)가 비어 있습니다.\n" +
                    "조치: 슬라이스가 끝난 시트 텍스처의 Assets/ 기준 상대 경로를 지정해주세요 " +
                    "(예: Assets/Generated/3_Confirmed/SpriteSheets/hero_sheet.png).");
            }

            string path = sheetPath.Trim().Replace('\\', '/');
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(path) == null)
            {
                throw new InvalidOperationException(
                    $"시트 텍스처를 찾을 수 없습니다: {path}\n" +
                    "원인: 경로가 잘못되었거나 Unity가 아직 임포트하지 않은 파일입니다.\n" +
                    "조치: 프로젝트 창에서 시트 텍스처의 경로를 확인해주세요 (Assets/ 기준 상대 경로).");
            }

            UnityEngine.Object[] representations = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
            var plan = new SpriteSheetClipPlan
            {
                sheetPath = path,
                sheetName = Path.GetFileNameWithoutExtension(path)
            };
            plan.outputFolder = $"{MCPToolFolders.AnimationsDir(MCPToolSettings.GetOrCreate())}/{plan.sheetName}";

            var byAction = new Dictionary<string, SpriteSheetActionGroup>(StringComparer.Ordinal);
            var frameNumbers = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (UnityEngine.Object representation in representations ?? new UnityEngine.Object[0])
            {
                var sprite = representation as Sprite;
                if (sprite == null)
                {
                    if (representation != null)
                    {
                        plan.skipped.Add($"\"{representation.name}\": Sprite가 아닌 서브 에셋이라 건너뛰었습니다.");
                    }

                    continue;
                }

                Match match = FrameNamePattern.Match(sprite.name);
                if (!match.Success)
                {
                    plan.skipped.Add(
                        $"\"{sprite.name}\": 이름이 \"{{동작}}_{{번호}}\" 규칙(예: walk_01)에 맞지 않아 건너뛰었습니다.");
                    continue;
                }

                string action = match.Groups["action"].Value;
                if (!int.TryParse(match.Groups["no"].Value, out int frameNo))
                {
                    plan.skipped.Add($"\"{sprite.name}\": 프레임 번호를 해석할 수 없어 건너뛰었습니다.");
                    continue;
                }

                if (!byAction.TryGetValue(action, out SpriteSheetActionGroup group))
                {
                    group = new SpriteSheetActionGroup
                    {
                        action = action,
                        clipPath = $"{plan.outputFolder}/{action}.anim",
                        loop = IsLoopByDefault(action)
                    };
                    group.clipExists = AssetDatabase.LoadAssetAtPath<AnimationClip>(group.clipPath) != null;
                    byAction[action] = group;
                    frameNumbers[action] = new List<int>();
                    order.Add(action);
                }

                group.Sprites.Add(sprite);
                frameNumbers[action].Add(frameNo);
            }

            foreach (string action in order)
            {
                SpriteSheetActionGroup group = byAction[action];
                List<int> numbers = frameNumbers[action];

                // 프레임 번호 오름차순 정렬 (LoadAllAssetRepresentationsAtPath의 반환 순서는 보장되지 않음)
                var indices = new List<int>();
                for (int i = 0; i < group.Sprites.Count; i++)
                {
                    indices.Add(i);
                }

                indices.Sort((a, b) => numbers[a].CompareTo(numbers[b]));
                var sorted = new List<Sprite>(group.Sprites.Count);
                foreach (int index in indices)
                {
                    sorted.Add(group.Sprites[index]);
                }

                group.Sprites = sorted;
                group.frameCount = sorted.Count;
                plan.groups.Add(group);
            }

            if (plan.groups.Count == 0)
            {
                throw new InvalidOperationException(
                    $"시트에서 클립으로 만들 스프라이트를 찾지 못했습니다: {path}\n" +
                    "원인: Sprite Mode=Multiple로 슬라이스되지 않았거나, 스프라이트 이름이 " +
                    "\"{동작}_{번호}\" 규칙(예: walk_01)이 아닙니다.\n" +
                    "조치: 위 [3. 시트 임포트]로 슬라이스를 먼저 적용한 시트를 지정해주세요." +
                    (plan.skipped.Count > 0 ? "\n건너뛴 항목:\n- " + string.Join("\n- ", plan.skipped) : string.Empty));
            }

            return plan;
        }

        /// <summary>
        /// 동작명의 기본 루프 여부를 반환합니다 (idle·walk·run 계열 ON, 그 외 OFF).
        /// </summary>
        /// <param name="action">동작명.</param>
        /// <returns>루프 재생이 기본이면 true.</returns>
        public static bool IsLoopByDefault(string action)
        {
            string normalized = (action ?? string.Empty).Trim().ToLowerInvariant();
            foreach (string prefix in LoopingActionPrefixes)
            {
                if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 대상 컴포넌트 이름("SpriteRenderer" / "Image")을 실제 타입으로 해석합니다.
        /// uGUI는 선택 의존이므로 어셈블리를 직접 참조하지 않고 타입 이름으로 찾습니다.
        /// </summary>
        /// <param name="targetComponent">대상 컴포넌트 이름. 비어 있으면 SpriteRenderer.</param>
        /// <returns>커브 바인딩에 사용할 컴포넌트 타입.</returns>
        /// <exception cref="InvalidOperationException">
        /// Image를 지정했지만 uGUI 패키지가 없어 타입을 찾을 수 없을 때 (원인·조치 안내 포함).
        /// </exception>
        public static Type ResolveTargetType(string targetComponent)
        {
            if (string.IsNullOrWhiteSpace(targetComponent) ||
                string.Equals(targetComponent, SpriteRendererComponent, StringComparison.OrdinalIgnoreCase))
            {
                return typeof(SpriteRenderer);
            }

            if (!string.Equals(targetComponent, ImageComponent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"대상 컴포넌트 값이 잘못되었습니다: \"{targetComponent}\"\n" +
                    $"조치: \"{SpriteRendererComponent}\" 또는 \"{ImageComponent}\" 중 하나를 지정해주세요.");
            }

            Type imageType = Type.GetType("UnityEngine.UI.Image, UnityEngine.UI");
            if (imageType == null)
            {
                throw new InvalidOperationException(
                    "Image 대상 클립을 만들 수 없습니다: uGUI(UnityEngine.UI) 어셈블리를 찾지 못했습니다.\n" +
                    "원인: 프로젝트에 uGUI 패키지(com.unity.ugui)가 설치되어 있지 않습니다.\n" +
                    "조치: Window > Package Manager에서 Unity UI(com.unity.ugui)를 설치하거나, " +
                    "대상 컴포넌트를 SpriteRenderer로 바꿔 다시 실행해주세요.");
            }

            return imageType;
        }

        /// <summary>
        /// 계획대로 동작별 AnimationClip을 만들고(있으면 갱신), 옵션에 따라 AnimatorController 구성과
        /// 대상 프리팹 연결까지 수행합니다.
        /// <para>
        /// 같은 경로에 클립이 이미 있으면 <b>에셋을 새로 만들지 않고</b> 스프라이트 커브·프레임 레이트·루프
        /// 설정만 덮어씁니다(컨트롤러가 물고 있는 참조와 사용자가 붙인 애니메이션 이벤트 보존).
        /// </para>
        /// <para>
        /// Animator는 프리팹 <b>루트</b>에 붙이고 커브 경로는 <paramref name="targetObjectPath"/>(루트 기준
        /// 계층 경로, 빈 문자열이면 루트 자신)를 사용합니다. 커브 경로는 Animator가 붙은 오브젝트 기준이므로
        /// 이렇게 해야 자식 오브젝트의 스프라이트도 그대로 재생됩니다.
        /// </para>
        /// </summary>
        /// <param name="plan"><see cref="Scan"/>으로 만든 계획 (동작명·루프·포함 여부 확정 상태).</param>
        /// <param name="frameRate">클립 프레임 레이트(fps). 1 미만이면 1로 보정합니다.</param>
        /// <param name="targetComponent">대상 컴포넌트 이름 ("SpriteRenderer" | "Image").</param>
        /// <param name="createController">true면 <c>{시트이름}.controller</c>를 만들고 동작별 State를 배치합니다.</param>
        /// <param name="targetPrefabPath">
        /// Animator + 컨트롤러를 연결할 프리팹 경로 (선택). <paramref name="createController"/>가 true일 때만 사용됩니다.
        /// </param>
        /// <param name="targetObjectPath">
        /// 스프라이트 컴포넌트가 있는 오브젝트의 프리팹 루트 기준 계층 경로 (선택, 빈 문자열이면 루트 자신).
        /// </param>
        /// <returns>생성·갱신 결과.</returns>
        /// <exception cref="InvalidOperationException">
        /// 계획이 비었거나, 생성 대상 동작이 하나도 없거나, 대상 컴포넌트 타입을 해석할 수 없을 때.
        /// </exception>
        public static SpriteSheetClipBuildResult Build(
            SpriteSheetClipPlan plan, int frameRate, string targetComponent, bool createController,
            string targetPrefabPath, string targetObjectPath)
        {
            if (plan == null || plan.groups == null || plan.groups.Count == 0)
            {
                throw new InvalidOperationException(
                    "클립 생성 계획이 비어 있습니다.\n조치: 먼저 슬라이스된 시트를 지정해 동작 스캔을 실행해주세요.");
            }

            Type targetType = ResolveTargetType(targetComponent);
            int fps = Mathf.Max(1, frameRate);
            string objectPath = (targetObjectPath ?? string.Empty).Trim().Replace('\\', '/').Trim('/');

            var result = new SpriteSheetClipBuildResult
            {
                sheetPath = plan.sheetPath,
                objectPath = objectPath
            };
            result.skipped.AddRange(plan.skipped);

            if (!MCPToolFolders.EnsureAssetFolder(plan.outputFolder) &&
                !AssetDatabase.IsValidFolder(plan.outputFolder))
            {
                throw new InvalidOperationException(
                    $"클립 출력 폴더를 만들 수 없습니다: {plan.outputFolder}\n" +
                    "원인: 경로가 프로젝트의 Assets 아래가 아니거나 폴더 이름에 쓸 수 없는 문자가 있습니다.\n" +
                    "조치: MCPToolSettings의 생성 결과 루트(generatedRootPath)를 확인해주세요.");
            }

            foreach (SpriteSheetActionGroup group in plan.groups)
            {
                if (!group.include)
                {
                    result.skipped.Add($"\"{group.action}\": 생성 대상에서 제외되어 클립을 만들지 않았습니다.");
                    continue;
                }

                if (group.Sprites == null || group.Sprites.Count == 0)
                {
                    result.skipped.Add($"\"{group.action}\": 프레임이 없어 클립을 만들지 않았습니다.");
                    continue;
                }

                group.created = WriteClip(group, targetType, objectPath, fps);
                result.groups.Add(group);
            }

            if (result.groups.Count == 0)
            {
                throw new InvalidOperationException(
                    "클립을 만들 동작이 하나도 없습니다.\n조치: 생성할 동작을 최소 1개 이상 체크해주세요.");
            }

            AssetDatabase.SaveAssets();

            if (createController)
            {
                result.controllerPath = ControllerPathForSheet(plan.sheetPath);
                AnimatorController controller = EnsureController(result);
                AssetDatabase.SaveAssets();

                if (!string.IsNullOrWhiteSpace(targetPrefabPath))
                {
                    result.prefabPath = targetPrefabPath.Trim().Replace('\\', '/');
                    result.prefabLinked = LinkPrefab(result, controller);
                }
            }
            else if (!string.IsNullOrWhiteSpace(targetPrefabPath))
            {
                result.skipped.Add(
                    "대상 프리팹이 지정되었지만 컨트롤러를 만들지 않아 프리팹에 연결하지 않았습니다. " +
                    "조치: [클립 + Animator 생성](MCP는 createController=true)으로 실행해주세요.");
            }

            AssetDatabase.SaveAssets();
            return result;
        }

        /// <summary>
        /// 동작 1개의 클립을 쓰고, 새로 만들었으면 true를 반환합니다.
        /// 기존 클립은 스프라이트 커브·프레임 레이트·루프 설정만 덮어쓰며,
        /// 대상이 바뀌어 더 이상 쓰이지 않는 이전 스프라이트 커브(다른 경로/컴포넌트)는 제거합니다.
        /// </summary>
        private static bool WriteClip(SpriteSheetActionGroup group, Type targetType, string objectPath, int frameRate)
        {
            var keys = new ObjectReferenceKeyframe[group.Sprites.Count];
            for (int i = 0; i < group.Sprites.Count; i++)
            {
                keys[i] = new ObjectReferenceKeyframe
                {
                    time = i / (float)frameRate,
                    value = group.Sprites[i]
                };
            }

            EditorCurveBinding binding = EditorCurveBinding.PPtrCurve(objectPath, targetType, SpritePropertyName);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(group.clipPath);
            bool created = clip == null;
            if (created)
            {
                clip = new AnimationClip();
                AssetDatabase.CreateAsset(clip, group.clipPath);
            }
            else
            {
                foreach (EditorCurveBinding old in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (old.propertyName == SpritePropertyName &&
                        (old.path != binding.path || old.type != binding.type))
                    {
                        AnimationUtility.SetObjectReferenceCurve(clip, old, null);
                    }
                }
            }

            clip.frameRate = frameRate;
            AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = group.loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            EditorUtility.SetDirty(clip);
            group.clipExists = true;
            return created;
        }

        /// <summary>
        /// AnimatorController를 만들거나(없을 때) 기존 컨트롤러에 <b>없는 State만</b> 추가합니다.
        /// 기존 트랜지션·파라미터·레이어는 건드리지 않습니다.
        /// </summary>
        private static AnimatorController EnsureController(SpriteSheetClipBuildResult result)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(result.controllerPath);
            bool created = controller == null;
            if (created)
            {
                controller = AnimatorController.CreateAnimatorControllerAtPath(result.controllerPath);
            }

            if (controller == null || controller.layers == null || controller.layers.Length == 0)
            {
                throw new InvalidOperationException(
                    $"AnimatorController를 준비할 수 없습니다: {result.controllerPath}\n" +
                    "원인: 컨트롤러 생성에 실패했거나 레이어가 하나도 없는 컨트롤러입니다.\n" +
                    "조치: 해당 경로의 컨트롤러를 삭제한 뒤 다시 실행하거나, 레이어를 1개 이상 만들어주세요.");
            }

            AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
            var states = new Dictionary<string, AnimatorState>(StringComparer.Ordinal);
            foreach (ChildAnimatorState child in stateMachine.states)
            {
                if (child.state != null)
                {
                    states[child.state.name] = child.state;
                }
            }

            foreach (SpriteSheetActionGroup group in result.groups)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(group.clipPath);
                if (states.TryGetValue(group.action, out AnimatorState existing))
                {
                    // 기존 State는 그대로 둔다 (트랜지션·설정 보존). 모션이 비어 있을 때만 방금 만든 클립을 연결한다.
                    if (existing.motion == null)
                    {
                        existing.motion = clip;
                    }

                    continue;
                }

                AnimatorState state = stateMachine.AddState(group.action);
                state.motion = clip;
                states[group.action] = state;
                result.addedStates.Add(group.action);
            }

            // 기본 State: 새로 만든 컨트롤러이거나 기본 State가 비어 있을 때만 지정한다 (idle → walk → 첫 동작).
            if (created || stateMachine.defaultState == null)
            {
                AnimatorState defaultState = PickDefaultState(states, result.groups);
                if (defaultState != null)
                {
                    stateMachine.defaultState = defaultState;
                }
            }

            EditorUtility.SetDirty(controller);
            return controller;
        }

        /// <summary>기본 State를 고릅니다 (idle 계열 → walk 계열 → 첫 동작 순).</summary>
        private static AnimatorState PickDefaultState(
            Dictionary<string, AnimatorState> states, List<SpriteSheetActionGroup> groups)
        {
            foreach (string preferred in new[] { "idle", "walk" })
            {
                foreach (SpriteSheetActionGroup group in groups)
                {
                    if (group.action.ToLowerInvariant().StartsWith(preferred, StringComparison.Ordinal) &&
                        states.TryGetValue(group.action, out AnimatorState state))
                    {
                        return state;
                    }
                }
            }

            return groups.Count > 0 && states.TryGetValue(groups[0].action, out AnimatorState first) ? first : null;
        }

        /// <summary>
        /// 대상 프리팹 루트에 Animator를 붙이고(있으면 재사용) 컨트롤러를 할당합니다.
        /// 프리팹 에셋을 직접 수정하며 <c>Undo</c> 등록 후 <c>PrefabUtility.SavePrefabAsset</c>으로 저장합니다.
        /// 실패 사유는 결과의 <c>skipped</c>에 남깁니다.
        /// </summary>
        private static bool LinkPrefab(SpriteSheetClipBuildResult result, AnimatorController controller)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.prefabPath);
            if (prefab == null)
            {
                result.skipped.Add(
                    $"대상 프리팹을 찾을 수 없어 Animator를 연결하지 못했습니다: {result.prefabPath}\n" +
                    "조치: 프리팹 경로(Assets/ 기준 상대 경로)를 확인해주세요.");
                return false;
            }

            // 커브 경로는 Animator가 붙은 오브젝트 기준이므로, 지정한 대상 오브젝트가 프리팹에 있는지 확인한다.
            if (!string.IsNullOrEmpty(result.objectPath) &&
                AssetApplier.FindTargetTransform(prefab, result.objectPath) == null)
            {
                result.skipped.Add(
                    $"프리팹 \"{result.prefabPath}\"에서 대상 오브젝트 경로를 찾지 못했습니다: \"{result.objectPath}\"\n" +
                    "조치: 프리팹 계층에 있는 오브젝트 경로를 지정해주세요 (클립은 그대로 만들어졌습니다).");
                return false;
            }

            var animator = prefab.GetComponent<Animator>();
            if (animator == null)
            {
                animator = Undo.AddComponent<Animator>(prefab);
            }

            if (animator == null)
            {
                result.skipped.Add(
                    $"프리팹 \"{result.prefabPath}\"에 Animator를 추가하지 못했습니다.\n" +
                    "조치: 프리팹이 읽기 전용(패키지 안 등)인지 확인해주세요.");
                return false;
            }

            Undo.RecordObject(animator, "MCPTools 애니메이터 컨트롤러 연결");
            animator.runtimeAnimatorController = controller;
            EditorUtility.SetDirty(animator);
            PrefabUtility.SavePrefabAsset(prefab);
            return true;
        }
    }
}
