using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPTools.Editor
{
    /// <summary>
    /// 4단계 적용 결과 1건입니다.
    /// </summary>
    public class ApplyResult
    {
        /// <summary>적용 성공 여부.</summary>
        public bool success;

        /// <summary>대상 프리팹 경로 (Assets/ 기준 상대 경로). 씬 항목이면 빈 문자열.</summary>
        public string prefabPath = string.Empty;

        /// <summary>대상 씬 경로 (Assets/ 기준 상대 경로). 씬 직접 배치 오브젝트 항목이면 비어 있지 않음.</summary>
        public string scenePath = string.Empty;

        /// <summary>대상 오브젝트 경로 (프리팹 항목은 프리팹 루트 기준, 씬 항목은 씬 루트 기준).</summary>
        public string objectPath = string.Empty;

        /// <summary>실제로 적용된 에셋 경로 (Assets/ 기준 상대 경로).</summary>
        public string appliedAssetPath = string.Empty;

        /// <summary>프리팹 루트 Animator에 연결한 AnimatorController 경로. 연결하지 않았으면 빈 문자열.</summary>
        public string linkedControllerPath = string.Empty;

        /// <summary>결과 메시지 (실패 시 사유).</summary>
        public string message = string.Empty;
    }

    /// <summary>
    /// 4단계 AssetApplier의 실제 적용 로직입니다. 확정된 생성물을 AssetList 항목에 기록된
    /// 대상 프리팹의 컴포넌트(Image.sprite / RawImage.texture / SpriteRenderer.sprite / AudioSource.clip,
    /// 오디오 항목은 targetComponent+targetField 지정 시 임의 컴포넌트의 직렬화 AudioClip 필드)에
    /// 할당하고 프리팹 에셋 자체를 저장합니다.
    ///
    /// [Undo 방식 선택 근거]
    /// PrefabUtility.LoadPrefabContents는 씬/에셋과 분리된 임시 복사본을 다루므로 Undo가 걸리지 않는다.
    /// 대신 AssetDatabase.LoadAssetAtPath&lt;GameObject&gt;로 임포트된 프리팹 에셋 자체를 얻어
    /// 대상 컴포넌트에 Undo.RecordObject를 등록한 뒤 값을 변경하고 PrefabUtility.SavePrefabAsset으로
    /// 저장하는 방식을 사용한다. 이 방식은 에디터에서 Ctrl+Z로 값이 실제로 되돌아가며
    /// (되돌린 상태는 다음 저장 시점에 파일에 반영됨), MCP 경로에서도 동일 코드를 공유할 수 있다.
    /// </summary>
    public static class AssetApplier
    {
        /// <summary>실패 사유에 나열할 서브 스프라이트 이름의 최대 개수입니다.</summary>
        private const int MaxListedSpriteNames = 10;

        /// <summary>
        /// 항목의 확정본 에셋 경로를 자동 탐색합니다.
        /// 항목에 <see cref="AssetListItem.spriteSheetPath"/>가 지정되어 있으면 자동 탐색 대신 그 경로를 사용합니다.
        /// 그 외에는 GenerationResults.json의 outputPath 기록을 우선 사용하고,
        /// 없으면 규칙 경로(Assets/Generated/3_Confirmed/Images/{id}.png, Audio/{id}.*, 구 위치도 함께 탐색)를 탐색합니다.
        /// </summary>
        /// <param name="settings">설정 객체.</param>
        /// <param name="item">대상 항목.</param>
        /// <returns>확정본 경로 (Assets/ 기준 상대 경로). 찾지 못하면 null.</returns>
        public static string FindConfirmedAssetPath(MCPToolSettings settings, AssetListItem item)
        {
            if (settings == null || item == null)
            {
                return null;
            }

            // 스프라이트 시트를 직접 지정한 항목: 시트는 항목별 확정본이 아니므로 자동 탐색 대상이 아니다.
            // (파일이 없으면 여기서 걸러내지 않고 ValidateItem이 "에셋 파일 없음"으로 안내한다.)
            if (!string.IsNullOrEmpty(item.spriteSheetPath))
            {
                return item.spriteSheetPath.Replace('\\', '/');
            }

            if (string.IsNullOrEmpty(item.id))
            {
                return null;
            }

            // 1) GenerationResults.json 기록 우선 (새 위치 3_Confirmed/ → 없으면 구 위치)
            string resultsPath = CandidateGenerator.ResolveResultsPathForRead(settings);
            if (File.Exists(resultsPath))
            {
                var doc = MiniJson.Deserialize(File.ReadAllText(resultsPath)) as Dictionary<string, object>;
                if (doc != null && doc.TryGetValue("results", out object listObj) && listObj is List<object> list)
                {
                    foreach (object entry in list)
                    {
                        var dict = entry as Dictionary<string, object>;
                        if (dict == null)
                        {
                            continue;
                        }

                        string id = dict.TryGetValue("assetItemId", out object idObj) && idObj is string s ? s : null;
                        string output = dict.TryGetValue("outputPath", out object outObj) && outObj is string o ? o : null;
                        if (id == item.id && !string.IsNullOrEmpty(output) && File.Exists(output))
                        {
                            return output.Replace('\\', '/');
                        }
                    }
                }
            }

            // 2) 규칙 경로 폴백 — 새 확정본 폴더를 먼저 보고, 없으면 구 위치(생성 루트 바로 아래)를 본다.
            foreach (string confirmedRoot in new[]
                     {
                         MCPToolFolders.ConfirmedRoot(settings),
                         MCPToolFolders.GeneratedRoot(settings)
                     })
            {
                if (item.assetType == "audio")
                {
                    string audioFolder = $"{confirmedRoot}/Audio";
                    if (Directory.Exists(audioFolder))
                    {
                        foreach (string ext in new[] { ".flac", ".wav", ".mp3", ".ogg" })
                        {
                            string path = $"{audioFolder}/{item.id}{ext}";
                            if (File.Exists(path))
                            {
                                return path;
                            }
                        }
                    }
                }
                else
                {
                    string imagePath = $"{confirmedRoot}/Images/{item.id}.png";
                    if (File.Exists(imagePath))
                    {
                        return imagePath;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 항목의 대상 컴포넌트 종류 이름을 판정합니다.
        /// audio → AudioSource(단, targetComponent 지정 시 해당 컴포넌트 타입 이름),
        /// UI(isUI/assetType=="ui") → Image 또는 RawImage(프리팹의 실제 컴포넌트 기준),
        /// 그 외 이미지 → SpriteRenderer.
        /// </summary>
        /// <param name="item">대상 항목.</param>
        /// <returns>기대 컴포넌트 이름 목록 (우선순위 순).</returns>
        public static string[] ExpectedComponentNames(AssetListItem item)
        {
            if (item.assetType == "audio")
            {
                return string.IsNullOrEmpty(item.targetComponent)
                    ? new[] { "AudioSource" }
                    : new[] { item.targetComponent };
            }

            if (item.IsUI || item.assetType == "ui")
            {
                return new[] { "Image", "RawImage" };
            }

            return new[] { "SpriteRenderer" };
        }

        /// <summary>
        /// 항목과 적용 에셋을 검증합니다: 프리팹 존재, 내부 오브젝트 경로 유효,
        /// 대상 컴포넌트 존재, 적용 에셋 존재·임포트 타입(Sprite/Texture2D/AudioClip) 일치.
        /// </summary>
        /// <param name="item">대상 항목.</param>
        /// <param name="assetPath">적용할 에셋 경로 (Assets/ 기준 상대 경로). null이면 확정본 자동 탐색.</param>
        /// <returns>실패 사유 목록. 비어 있으면 검증 통과.</returns>
        public static List<string> ValidateItem(AssetListItem item, string assetPath)
        {
            var reasons = new List<string>();
            if (item == null)
            {
                reasons.Add("항목이 null입니다.");
                return reasons;
            }

            // 오디오 임의 필드 대상: targetComponent/targetField는 반드시 함께 지정해야 한다.
            if (item.assetType == "audio"
                && string.IsNullOrEmpty(item.targetComponent) != string.IsNullOrEmpty(item.targetField))
            {
                reasons.Add(
                    "오디오 임의 필드 적용은 targetComponent(컴포넌트 타입 이름)와 targetField(직렬화 필드 경로)를 " +
                    "모두 지정해야 합니다. 1단계 목록을 확인해주세요.");
            }

            // 프리팹/씬 / 내부 경로 / 컴포넌트
            Component component = null;
            if (item.IsSceneItem)
            {
                // 씬 항목: 씬 파일 존재와 (열려 있는 경우에 한해) 오브젝트·컴포넌트를 검증한다.
                // 열려 있지 않은 씬은 검증 단계에서 열지 않고(비용·부작용 방지) 적용 시점에 확인한다.
                if (!string.IsNullOrEmpty(item.targetPrefabPath))
                {
                    reasons.Add("targetPrefabPath와 targetScenePath가 동시에 지정되었습니다 (상호 배타). 1단계 목록을 확인해주세요.");
                }

                if (!File.Exists(item.targetScenePath))
                {
                    reasons.Add($"씬 파일을 찾을 수 없습니다: \"{item.targetScenePath}\"");
                }
                else if (string.IsNullOrEmpty(item.targetObjectPath))
                {
                    reasons.Add("씬 항목은 대상 오브젝트 경로(targetObjectPath, 씬 루트 기준)가 필요합니다.");
                }
                else
                {
                    Scene scene = SceneManager.GetSceneByPath(item.targetScenePath.Replace('\\', '/'));
                    if (scene.isLoaded)
                    {
                        Transform target = FindSceneTransform(scene, item.targetObjectPath);
                        if (target == null)
                        {
                            reasons.Add($"씬 내부 오브젝트 경로를 찾을 수 없습니다: \"{item.targetObjectPath}\"");
                        }
                        else
                        {
                            component = FindTargetComponent(target, item);
                            if (component == null)
                            {
                                reasons.Add(
                                    $"대상 오브젝트 \"{target.name}\"에 기대 컴포넌트({string.Join("/", ExpectedComponentNames(item))})가 없습니다.");
                            }
                        }
                    }
                }
            }
            else if (string.IsNullOrEmpty(item.targetPrefabPath))
            {
                reasons.Add("대상 경로(targetPrefabPath 또는 targetScenePath)가 지정되지 않았습니다. 1단계 목록을 확인해주세요.");
            }
            else
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.targetPrefabPath);
                if (prefab == null)
                {
                    reasons.Add($"프리팹을 찾을 수 없습니다: \"{item.targetPrefabPath}\"");
                }
                else
                {
                    Transform target = FindTargetTransform(prefab, item.targetObjectPath);
                    if (target == null)
                    {
                        reasons.Add($"프리팹 내부 오브젝트 경로를 찾을 수 없습니다: \"{item.targetObjectPath}\"");
                    }
                    else
                    {
                        component = FindTargetComponent(target, item);
                        if (component == null)
                        {
                            reasons.Add(
                                $"대상 오브젝트 \"{target.name}\"에 기대 컴포넌트({string.Join("/", ExpectedComponentNames(item))})가 없습니다.");
                        }
                    }
                }
            }

            // 오디오 임의 필드 대상: 컴포넌트를 찾은 경우 직렬화 필드 존재·타입을 검증한다.
            if (component != null && item.HasCustomAudioTarget)
            {
                ValidateAudioField(component, item.targetField, reasons);
            }

            // 적용 에셋 존재·임포트 타입
            if (string.IsNullOrEmpty(assetPath))
            {
                assetPath = FindConfirmedAssetPath(MCPToolSettings.GetOrCreate(), item);
                if (string.IsNullOrEmpty(assetPath))
                {
                    reasons.Add($"항목 \"{item.id}\"의 확정본을 찾을 수 없습니다. 3단계(ComfyUI Generator)에서 먼저 확정해주세요.");
                    return reasons;
                }
            }

            if (!File.Exists(assetPath))
            {
                reasons.Add($"적용할 에셋 파일이 없습니다: \"{assetPath}\"");
                return reasons;
            }

            string componentName = component != null
                ? component.GetType().Name
                : ExpectedComponentNames(item)[0];
            if (item.assetType == "audio")
            {
                // AudioSource.clip과 임의 컴포넌트 필드 대상 모두 AudioClip 임포트가 필요하다.
                componentName = "AudioSource";
            }

            switch (componentName)
            {
                case "AudioSource":
                    if (AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath) == null)
                    {
                        reasons.Add($"에셋이 AudioClip으로 임포트되지 않았습니다: \"{assetPath}\"");
                    }

                    break;

                case "RawImage":
                    if (AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath) == null)
                    {
                        reasons.Add($"에셋이 Texture2D로 임포트되지 않았습니다: \"{assetPath}\"");
                    }

                    // RawImage는 Texture를 받으므로 서브 스프라이트를 지정해도 반영되지 않는다.
                    // 말없이 시트 전체가 적용되면 원인을 찾기 어려우므로 여기서 걸러 안내한다.
                    if (!string.IsNullOrEmpty(item.spriteName))
                    {
                        reasons.Add(
                            $"대상이 RawImage여서 스프라이트 이름(\"{item.spriteName}\")을 적용할 수 없습니다. " +
                            "특정 프레임을 쓰려면 대상을 Image로 바꾸거나, 시트 전체를 쓰려면 스프라이트 이름을 비워주세요.");
                    }

                    break;

                default: // Image / SpriteRenderer → Sprite 필요
                    if (FindItemSprite(assetPath, item) == null)
                    {
                        // 이름을 지정한 항목은 그 이름이 없는 것이고, 아니면 Sprite 임포트 자체가 안 된 것이다.
                        reasons.Add(!string.IsNullOrEmpty(item.spriteName)
                            ? $"에셋 \"{assetPath}\"에서 스프라이트 \"{item.spriteName}\"을(를) 찾을 수 없습니다. " +
                              DescribeAvailableSprites(assetPath)
                            : $"에셋이 Sprite로 임포트되지 않았습니다: \"{assetPath}\". " +
                              "Texture Type을 Sprite (2D and UI)로 변경해주세요.");
                    }

                    break;
            }

            // 연결할 컨트롤러가 있는 항목(직접 지정 또는 시트 기준 자동)만 추가 검증한다.
            if (!string.IsNullOrEmpty(item.animatorControllerPath) && item.IsSceneItem)
            {
                reasons.Add(
                    "씬에 직접 배치된 오브젝트에는 AnimatorController를 자동 연결하지 않습니다. " +
                    "조치: 프리팹 대상 항목에서 지정하거나, 씬에서 직접 Animator에 컨트롤러를 연결해주세요.");
            }
            else
            {
                string controllerPath = ResolveAnimatorControllerPath(item);
                if (!string.IsNullOrEmpty(controllerPath))
                {
                    ValidateAnimatorController(item, controllerPath, reasons);
                }
            }

            return reasons;
        }

        /// <summary>
        /// 항목에 연결할 AnimatorController 경로를 정합니다.
        /// <see cref="AssetListItem.animatorControllerPath"/>가 있으면 그 값을 쓰고,
        /// 없고 스프라이트 시트를 지정했다면 그 시트에서 만든 컨트롤러
        /// (<c>{확정본}/Animations/{시트이름}/{시트이름}.controller</c>)를 <b>자동으로</b> 씁니다.
        /// 한 시트의 클립·컨트롤러는 한 벌뿐이므로, 시트를 고르면 애니메이션까지 함께 적용됩니다.
        /// </summary>
        /// <param name="item">대상 항목.</param>
        /// <returns>연결할 컨트롤러 경로. 없으면 빈 문자열.</returns>
        public static string ResolveAnimatorControllerPath(AssetListItem item)
        {
            if (item == null || item.IsSceneItem)
            {
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(item.animatorControllerPath))
            {
                return item.animatorControllerPath.Replace('\\', '/');
            }

            if (string.IsNullOrEmpty(item.spriteSheetPath))
            {
                return string.Empty;
            }

            // 시트에서 만든 컨트롤러가 실제로 있을 때만 자동 연결한다 (없으면 스프라이트만 적용).
            string auto = SpriteSheetClipBuilder.ControllerPathForSheet(item.spriteSheetPath);
            return AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(auto) != null ? auto : string.Empty;
        }

        /// <summary>
        /// 항목에 지정한 AnimatorController 연결 대상을 검증합니다.
        /// 컨트롤러 에셋 존재 여부와, 컨트롤러가 참조하는 클립의 커브 경로가 프리팹 계층에 실제로 있는지 확인합니다
        /// (커브 경로는 Animator가 붙는 프리팹 루트 기준이라, 경로가 어긋나면 재생해도 아무 일이 일어나지 않습니다).
        /// </summary>
        private static void ValidateAnimatorController(
            AssetListItem item, string controllerPath, List<string> reasons)
        {
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controllerPath);
            if (controller == null)
            {
                reasons.Add(
                    $"AnimatorController를 찾을 수 없습니다: \"{controllerPath}\". " +
                    "조치: 스프라이트 시트 창의 [클립 + Animator 생성]으로 컨트롤러를 먼저 만들거나 경로를 확인해주세요.");
                return;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.targetPrefabPath);
            if (prefab == null)
            {
                return; // 프리팹 자체 문제는 위에서 이미 사유로 남았다.
            }

            var missing = new List<string>();
            foreach (AnimationClip clip in controller.animationClips)
            {
                if (clip == null)
                {
                    continue;
                }

                foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    string bindingPath = binding.path ?? string.Empty;
                    if (FindTargetTransform(prefab, bindingPath) == null && !missing.Contains(bindingPath))
                    {
                        missing.Add(bindingPath);
                    }
                }
            }

            if (missing.Count > 0)
            {
                var shown = new List<string>();
                foreach (string path in missing)
                {
                    shown.Add(string.IsNullOrEmpty(path) ? "(루트)" : path);
                }

                reasons.Add(
                    $"컨트롤러 \"{controllerPath}\"의 클립이 참조하는 오브젝트 경로가 프리팹 \"{item.targetPrefabPath}\"에 없습니다: " +
                    $"{string.Join(", ", shown)}. 그대로 연결하면 애니메이션이 재생돼도 스프라이트가 바뀌지 않습니다.\n" +
                    "조치: 스프라이트 시트 창에서 이 프리팹의 대상 오브젝트를 지정해 클립을 다시 만들어주세요.");
            }
        }

        /// <summary>
        /// 항목 종류에 따라 프리팹/씬 적용으로 분기하는 진입점입니다.
        /// targetScenePath가 채워진 항목은 <see cref="ApplyToScene"/>, 그 외는 <see cref="ApplyToPrefab"/>을 호출합니다.
        /// </summary>
        /// <param name="item">대상 항목.</param>
        /// <param name="assetPath">적용할 에셋 경로 (Assets/ 기준 상대 경로). null이면 확정본 자동 탐색.</param>
        /// <returns>적용 결과.</returns>
        public static ApplyResult Apply(AssetListItem item, string assetPath)
        {
            return item != null && item.IsSceneItem
                ? ApplyToScene(item, assetPath)
                : ApplyToPrefab(item, assetPath);
        }

        /// <summary>
        /// 확정본을 대상 프리팹 에셋에 적용합니다. 검증 실패 시 적용하지 않고 사유를 반환합니다.
        /// 항목에 <see cref="AssetListItem.animatorControllerPath"/>가 있으면 프리팹 루트 Animator 연결까지
        /// 같은 저장에 함께 수행합니다.
        /// Undo.RecordObject 등록 후 값을 변경하므로 에디터에서 Ctrl+Z로 되돌릴 수 있습니다.
        /// </summary>
        /// <param name="item">대상 항목.</param>
        /// <param name="assetPath">적용할 에셋 경로 (Assets/ 기준 상대 경로). null이면 확정본 자동 탐색.</param>
        /// <returns>적용 결과.</returns>
        public static ApplyResult ApplyToPrefab(AssetListItem item, string assetPath)
        {
            var result = new ApplyResult
            {
                prefabPath = item != null ? item.targetPrefabPath : string.Empty,
                objectPath = item != null ? item.targetObjectPath : string.Empty
            };

            if (string.IsNullOrEmpty(assetPath))
            {
                assetPath = FindConfirmedAssetPath(MCPToolSettings.GetOrCreate(), item);
            }

            List<string> reasons = ValidateItem(item, assetPath);
            if (reasons.Count > 0)
            {
                result.message = string.Join(" / ", reasons);
                return result;
            }

            result.appliedAssetPath = assetPath.Replace('\\', '/');

            // 임포트된 프리팹 에셋 자체를 수정한다 (씬 인스턴스·프리팹 스테이지는 건드리지 않음).
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.targetPrefabPath);
            Transform target = FindTargetTransform(prefab, item.targetObjectPath);
            Component component = FindTargetComponent(target, item);

            try
            {
                AssignAsset(component, assetPath, item);

                // 컨트롤러 연결은 같은 프리팹 수정이므로 저장 전에 함께 처리해 한 번만 저장한다.
                string linked = LinkAnimatorController(item, prefab);
                PrefabUtility.SavePrefabAsset(prefab);

                result.linkedControllerPath = linked ?? string.Empty;
                result.success = true;
                result.message =
                    $"적용 완료: {item.targetPrefabPath} → {component.GetType().Name} ({result.appliedAssetPath})" +
                    (linked != null ? $" + Animator 연결 ({linked})" : string.Empty);
            }
            catch (Exception e)
            {
                result.message = $"적용 중 오류가 발생했습니다: {e.Message}";
            }

            return result;
        }

        /// <summary>
        /// 항목에 연결할 컨트롤러가 있으면(<see cref="ResolveAnimatorControllerPath"/> — 직접 지정 또는 시트 기준 자동)
        /// 프리팹 루트에 Animator를 붙이고(이미 있으면 그대로 재사용) 컨트롤러를 연결합니다.
        /// <c>Undo</c>에 등록하며, 프리팹 저장은 호출자가 <c>PrefabUtility.SavePrefabAsset</c>으로 한 번에 수행합니다.
        /// (커브 경로 유효성은 <see cref="ValidateItem"/>에서 미리 검사합니다)
        /// </summary>
        /// <returns>연결한 컨트롤러 경로. 연결할 컨트롤러가 없으면 null.</returns>
        /// <exception cref="InvalidOperationException">컨트롤러를 로드할 수 없거나 Animator를 추가하지 못한 경우.</exception>
        private static string LinkAnimatorController(AssetListItem item, GameObject prefab)
        {
            string controllerPath = ResolveAnimatorControllerPath(item);
            if (prefab == null || string.IsNullOrEmpty(controllerPath))
            {
                return null;
            }

            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controllerPath);
            if (controller == null)
            {
                throw new InvalidOperationException(
                    $"AnimatorController를 로드할 수 없습니다: \"{controllerPath}\". 경로를 확인해주세요.");
            }

            var animator = prefab.GetComponent<Animator>();
            if (animator == null)
            {
                animator = Undo.AddComponent<Animator>(prefab);
            }

            if (animator == null)
            {
                throw new InvalidOperationException(
                    $"프리팹 \"{item.targetPrefabPath}\"에 Animator를 추가하지 못했습니다. " +
                    "프리팹이 읽기 전용(패키지 안 등)인지 확인해주세요.");
            }

            Undo.RecordObject(animator, $"MCPTools 애니메이터 컨트롤러 연결 ({item.id})");
            animator.runtimeAnimatorController = controller;
            EditorUtility.SetDirty(animator);
            return controllerPath;
        }

        /// <summary>
        /// 확정본을 씬에 직접 배치된 오브젝트에 적용합니다.
        /// 씬이 이미 열려 있으면 그대로 사용해 값만 변경하고 씬을 dirty로 표시합니다
        /// (저장은 사용자가 수행 — 열린 씬에서는 Ctrl+Z로 되돌릴 수 있습니다).
        /// 열려 있지 않으면 Additive로 열어 적용 후 <see cref="EditorSceneManager.SaveScene(Scene)"/>로 저장하고 닫습니다.
        /// </summary>
        /// <param name="item">대상 항목 (targetScenePath가 채워진 씬 항목).</param>
        /// <param name="assetPath">적용할 에셋 경로 (Assets/ 기준 상대 경로). null이면 확정본 자동 탐색.</param>
        /// <returns>적용 결과.</returns>
        public static ApplyResult ApplyToScene(AssetListItem item, string assetPath)
        {
            var result = new ApplyResult
            {
                scenePath = item != null ? item.targetScenePath : string.Empty,
                objectPath = item != null ? item.targetObjectPath : string.Empty
            };

            if (string.IsNullOrEmpty(assetPath))
            {
                assetPath = FindConfirmedAssetPath(MCPToolSettings.GetOrCreate(), item);
            }

            List<string> reasons = ValidateItem(item, assetPath);
            if (reasons.Count > 0)
            {
                result.message = string.Join(" / ", reasons);
                return result;
            }

            string scenePath = item.targetScenePath.Replace('\\', '/');
            Scene scene = SceneManager.GetSceneByPath(scenePath);
            bool openedHere = false;
            if (!scene.isLoaded)
            {
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                openedHere = true;
            }

            try
            {
                ApplyResult inner = ApplyToOpenScene(item, assetPath, scene);
                if (inner.success && openedHere)
                {
                    EditorSceneManager.SaveScene(scene);
                }

                return inner;
            }
            catch (Exception e)
            {
                result.message = $"적용 중 오류가 발생했습니다: {e.Message}";
                return result;
            }
            finally
            {
                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        /// <summary>
        /// 항목들을 순차 적용합니다 (프리팹/씬 자동 분기). 같은 씬을 대상으로 하는 항목들은 묶어서
        /// 씬을 한 번만 열어 처리한 뒤 저장·닫기하므로 일괄 적용 시 씬 열기 비용을 줄입니다.
        /// </summary>
        /// <param name="items">대상 항목 목록.</param>
        /// <param name="assetPaths">항목별 적용 에셋 경로 (items와 같은 길이. 요소가 null이면 확정본 자동 탐색).</param>
        /// <returns>items와 같은 순서의 적용 결과 목록.</returns>
        public static List<ApplyResult> ApplyBatch(List<AssetListItem> items, IList<string> assetPaths)
        {
            var results = new ApplyResult[items != null ? items.Count : 0];
            if (items == null || items.Count == 0)
            {
                return new List<ApplyResult>(results);
            }

            // 씬 항목을 씬 경로별로 묶는다 (프리팹 항목은 개별 처리).
            var sceneGroups = new Dictionary<string, List<int>>();
            for (int i = 0; i < items.Count; i++)
            {
                AssetListItem item = items[i];
                string path = i < (assetPaths != null ? assetPaths.Count : 0) ? assetPaths[i] : null;
                if (item != null && item.IsSceneItem)
                {
                    string key = item.targetScenePath.Replace('\\', '/');
                    if (!sceneGroups.TryGetValue(key, out List<int> indices))
                    {
                        sceneGroups[key] = indices = new List<int>();
                    }

                    indices.Add(i);
                }
                else
                {
                    results[i] = ApplyToPrefab(item, path);
                }
            }

            foreach (KeyValuePair<string, List<int>> group in sceneGroups)
            {
                ApplySceneGroup(group.Key, group.Value, items, assetPaths, results);
            }

            return new List<ApplyResult>(results);
        }

        /// <summary>같은 씬을 대상으로 하는 항목들을 씬을 한 번만 열어 순차 적용합니다.</summary>
        private static void ApplySceneGroup(
            string scenePath, List<int> indices, List<AssetListItem> items, IList<string> assetPaths,
            ApplyResult[] results)
        {
            Scene scene = SceneManager.GetSceneByPath(scenePath);
            bool openedHere = false;
            bool sceneMissing = !File.Exists(scenePath);
            if (!sceneMissing && !scene.isLoaded)
            {
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                openedHere = true;
            }

            try
            {
                bool anySuccess = false;
                foreach (int i in indices)
                {
                    AssetListItem item = items[i];
                    string assetPath = i < (assetPaths != null ? assetPaths.Count : 0) ? assetPaths[i] : null;
                    if (sceneMissing)
                    {
                        results[i] = new ApplyResult
                        {
                            scenePath = scenePath,
                            objectPath = item != null ? item.targetObjectPath : string.Empty,
                            message = $"씬 파일을 찾을 수 없습니다: \"{scenePath}\""
                        };
                        continue;
                    }

                    if (string.IsNullOrEmpty(assetPath))
                    {
                        assetPath = FindConfirmedAssetPath(MCPToolSettings.GetOrCreate(), item);
                    }

                    List<string> reasons = ValidateItem(item, assetPath);
                    if (reasons.Count > 0)
                    {
                        results[i] = new ApplyResult
                        {
                            scenePath = scenePath,
                            objectPath = item.targetObjectPath,
                            message = string.Join(" / ", reasons)
                        };
                        continue;
                    }

                    results[i] = ApplyToOpenScene(item, assetPath, scene);
                    anySuccess |= results[i].success;
                }

                if (anySuccess && openedHere)
                {
                    EditorSceneManager.SaveScene(scene);
                }
            }
            finally
            {
                if (openedHere)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        /// <summary>이미 열려 있는 씬에서 항목 1개를 적용합니다 (저장/닫기는 호출자 책임).</summary>
        private static ApplyResult ApplyToOpenScene(AssetListItem item, string assetPath, Scene scene)
        {
            var result = new ApplyResult
            {
                scenePath = item.targetScenePath.Replace('\\', '/'),
                objectPath = item.targetObjectPath,
                appliedAssetPath = assetPath.Replace('\\', '/')
            };

            Transform target = FindSceneTransform(scene, item.targetObjectPath);
            if (target == null)
            {
                result.message = $"씬 내부 오브젝트 경로를 찾을 수 없습니다: \"{item.targetObjectPath}\"";
                return result;
            }

            Component component = FindTargetComponent(target, item);
            if (component == null)
            {
                result.message =
                    $"대상 오브젝트 \"{target.name}\"에 기대 컴포넌트({string.Join("/", ExpectedComponentNames(item))})가 없습니다.";
                return result;
            }

            try
            {
                AssignAsset(component, assetPath, item);
                EditorSceneManager.MarkSceneDirty(scene);

                result.success = true;
                result.message =
                    $"적용 완료: {result.scenePath} → {component.GetType().Name} ({result.appliedAssetPath})";
            }
            catch (Exception e)
            {
                result.message = $"적용 중 오류가 발생했습니다: {e.Message}";
            }

            return result;
        }

        /// <summary>
        /// 씬 루트 기준 계층 경로("루트이름/자식/...")로 씬 내 Transform을 찾습니다.
        /// </summary>
        /// <param name="scene">대상 씬 (열려 있어야 함).</param>
        /// <param name="objectPath">씬 루트 오브젝트부터의 계층 경로.</param>
        /// <returns>대상 Transform. 없으면 null.</returns>
        public static Transform FindSceneTransform(Scene scene, string objectPath)
        {
            if (!scene.isLoaded || string.IsNullOrEmpty(objectPath))
            {
                return null;
            }

            int slash = objectPath.IndexOf('/');
            string rootName = slash >= 0 ? objectPath.Substring(0, slash) : objectPath;
            string rest = slash >= 0 ? objectPath.Substring(slash + 1) : null;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name != rootName)
                {
                    continue;
                }

                Transform found = string.IsNullOrEmpty(rest) ? root.transform : root.transform.Find(rest);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// 오디오 임의 필드 대상의 직렬화 필드가 존재하고 AudioClip을 받을 수 있는
        /// ObjectReference인지 검증해 실패 사유를 reasons에 추가합니다.
        /// </summary>
        private static void ValidateAudioField(Component component, string fieldPath, List<string> reasons)
        {
            var serialized = new SerializedObject(component);
            SerializedProperty property = serialized.FindProperty(fieldPath);
            if (property == null)
            {
                reasons.Add(
                    $"컴포넌트 \"{component.GetType().Name}\"에서 직렬화 필드 \"{fieldPath}\"를 찾을 수 없습니다. " +
                    "필드 이름(SerializedProperty 경로)과 [SerializeField]/public 여부를 확인해주세요.");
                return;
            }

            if (property.propertyType != SerializedPropertyType.ObjectReference
                || (property.type != "PPtr<$AudioClip>" && property.type != "PPtr<$Object>"))
            {
                reasons.Add(
                    $"컴포넌트 \"{component.GetType().Name}\"의 필드 \"{fieldPath}\"는 AudioClip 참조 필드가 아닙니다 " +
                    $"(실제 타입: {property.type}).");
            }
        }

        /// <summary>
        /// 텍스처 에셋에 포함된 서브 스프라이트 이름 목록을 반환합니다
        /// (Sprite Mode가 Multiple인 스프라이트 시트의 프레임 이름들. Single이면 스프라이트 1개).
        /// </summary>
        /// <param name="assetPath">텍스처 에셋 경로 (Assets/ 기준 상대 경로).</param>
        /// <returns>스프라이트 이름 목록 (없으면 빈 목록).</returns>
        public static List<string> GetSpriteNames(string assetPath)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(assetPath))
            {
                return names;
            }

            foreach (UnityEngine.Object representation in AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath))
            {
                if (representation is Sprite sprite)
                {
                    names.Add(sprite.name);
                }
            }

            return names;
        }

        /// <summary>
        /// 텍스처 에셋 안에서 이름이 일치하는 서브 스프라이트를 찾습니다.
        /// </summary>
        /// <param name="assetPath">텍스처(시트) 에셋 경로 (Assets/ 기준 상대 경로).</param>
        /// <param name="spriteName">찾을 스프라이트 이름 (예: "walk_03").</param>
        /// <returns>일치하는 Sprite. 없으면 null.</returns>
        public static Sprite FindSubSprite(string assetPath, string spriteName)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(spriteName))
            {
                return null;
            }

            foreach (UnityEngine.Object representation in AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath))
            {
                if (representation is Sprite sprite && sprite.name == spriteName)
                {
                    return sprite;
                }
            }

            return null;
        }

        /// <summary>
        /// 실패 사유에 덧붙일 "사용 가능한 스프라이트 이름" 안내 문구를 만듭니다
        /// (앞에서부터 최대 <see cref="MaxListedSpriteNames"/>개 + 나머지 개수).
        /// </summary>
        private static string DescribeAvailableSprites(string assetPath)
        {
            List<string> names = GetSpriteNames(assetPath);
            if (names.Count == 0)
            {
                return "이 에셋에는 서브 스프라이트가 없습니다. " +
                       "Texture Type을 Sprite (2D and UI), Sprite Mode를 Multiple로 두고 슬라이스했는지 확인해주세요.";
            }

            int shown = Mathf.Min(names.Count, MaxListedSpriteNames);
            string listed = string.Join(", ", names.GetRange(0, shown));
            return names.Count > shown
                ? $"사용 가능한 이름({names.Count}개): {listed} 외 {names.Count - shown}개"
                : $"사용 가능한 이름({names.Count}개): {listed}";
        }

        /// <summary>
        /// 항목에 적용할 Sprite를 찾습니다 (검증·미리보기·적용 공용, 실패해도 예외를 던지지 않습니다).
        /// <list type="number">
        /// <item><see cref="AssetListItem.spriteName"/>이 있으면 그 이름의 서브 스프라이트</item>
        /// <item>없으면 에셋 전체를 Sprite로 로드 (단일 스프라이트 이미지)</item>
        /// <item>그것도 없으면(Sprite Mode=Multiple 시트) <b>첫 번째 서브 스프라이트</b></item>
        /// </list>
        /// 3번 덕분에 스프라이트 시트를 프레임 이름 없이 지정해도 첫 프레임이 적용됩니다
        /// (Animator를 연결하면 재생 시 프레임이 덮어써지므로 초기 표시용으로 충분합니다).
        /// </summary>
        /// <param name="assetPath">적용할 에셋 경로 (Assets/ 기준 상대 경로).</param>
        /// <param name="item">대상 항목. null이면 에셋 전체 → 첫 서브 스프라이트 순으로 찾습니다.</param>
        /// <returns>적용할 Sprite. 찾지 못하면 null.</returns>
        public static Sprite FindItemSprite(string assetPath, AssetListItem item)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            if (item != null && !string.IsNullOrEmpty(item.spriteName))
            {
                return FindSubSprite(assetPath, item.spriteName);
            }

            Sprite whole = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (whole != null)
            {
                return whole;
            }

            foreach (UnityEngine.Object representation in AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath))
            {
                if (representation is Sprite sprite)
                {
                    return sprite;
                }
            }

            return null;
        }

        /// <summary>
        /// 항목에 맞는 Sprite를 로드합니다 (<see cref="FindItemSprite"/> 규칙).
        /// 찾지 못하면 원인·사용 가능한 이름을 담아 예외를 던집니다.
        /// </summary>
        private static Sprite ResolveSprite(string assetPath, AssetListItem item)
        {
            Sprite sprite = FindItemSprite(assetPath, item);
            if (sprite == null)
            {
                string what = item != null && !string.IsNullOrEmpty(item.spriteName)
                    ? $"스프라이트 \"{item.spriteName}\"을(를)"
                    : "적용할 스프라이트를";
                throw new InvalidOperationException(
                    $"에셋 \"{assetPath}\"에서 {what} 찾을 수 없습니다. " + DescribeAvailableSprites(assetPath));
            }

            return sprite;
        }

        /// <summary>
        /// 컴포넌트 종류에 맞게 에셋을 할당합니다 (Undo.RecordObject 등록 포함, 프리팹/씬 공용).
        /// 오디오 임의 필드 대상(targetComponent+targetField)은 해당 직렬화 필드에 AudioClip을 할당합니다.
        /// 항목에 spriteName이 지정된 경우 Image/SpriteRenderer에는 시트 안의 해당 서브 스프라이트를 할당합니다.
        /// </summary>
        private static void AssignAsset(Component component, string assetPath, AssetListItem item)
        {
            Undo.RecordObject(component, $"MCPTools 에셋 적용 ({item.id})");

            if (item.HasCustomAudioTarget)
            {
                SetObjectProperty(component, item.targetField, AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath));
                EditorUtility.SetDirty(component);
                return;
            }

            switch (component.GetType().Name)
            {
                case "AudioSource":
                    ((AudioSource)component).clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
                    break;

                case "SpriteRenderer":
                    ((SpriteRenderer)component).sprite = ResolveSprite(assetPath, item);
                    break;

                case "RawImage":
                    // uGUI 어셈블리 참조 없이 SerializedProperty로 할당한다.
                    SetObjectProperty(component, "m_Texture", AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath));
                    break;

                default: // Image
                    SetObjectProperty(component, "m_Sprite", ResolveSprite(assetPath, item));
                    break;
            }

            EditorUtility.SetDirty(component);
        }

        /// <summary>
        /// 항목의 대상 컴포넌트가 현재 참조하는 값(Sprite/Texture/AudioClip)을 반환합니다 (창의 미리보기용).
        /// </summary>
        /// <param name="item">대상 항목.</param>
        /// <returns>현재 참조 오브젝트. 대상이 없거나 비어 있으면 null.</returns>
        public static UnityEngine.Object GetCurrentValue(AssetListItem item)
        {
            if (item == null)
            {
                return null;
            }

            Component component = null;
            if (item.IsSceneItem)
            {
                // 씬 항목은 씬이 이미 열려 있는 경우에만 미리보기를 제공한다 (미리보기 때문에 씬을 열지 않음).
                Scene scene = SceneManager.GetSceneByPath(item.targetScenePath.Replace('\\', '/'));
                Transform sceneTarget = scene.isLoaded ? FindSceneTransform(scene, item.targetObjectPath) : null;
                component = sceneTarget != null ? FindTargetComponent(sceneTarget, item) : null;
            }
            else if (!string.IsNullOrEmpty(item.targetPrefabPath))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.targetPrefabPath);
                Transform target = prefab != null ? FindTargetTransform(prefab, item.targetObjectPath) : null;
                component = target != null ? FindTargetComponent(target, item) : null;
            }
            if (component == null)
            {
                return null;
            }

            if (item.HasCustomAudioTarget)
            {
                return GetObjectProperty(component, item.targetField);
            }

            switch (component.GetType().Name)
            {
                case "AudioSource":
                    return ((AudioSource)component).clip;
                case "SpriteRenderer":
                    return ((SpriteRenderer)component).sprite;
                case "RawImage":
                    return GetObjectProperty(component, "m_Texture");
                default: // Image
                    return GetObjectProperty(component, "m_Sprite");
            }
        }

        /// <summary>
        /// 프리팹 루트 기준 내부 오브젝트 경로를 탐색합니다.
        /// 빈 경로는 루트 자신이며, "루트이름/자식/..." 형태도 허용합니다.
        /// </summary>
        /// <param name="prefab">프리팹 루트.</param>
        /// <param name="objectPath">내부 경로 (슬래시 구분).</param>
        /// <returns>대상 Transform. 없으면 null.</returns>
        public static Transform FindTargetTransform(GameObject prefab, string objectPath)
        {
            if (prefab == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(objectPath) || objectPath == prefab.name)
            {
                return prefab.transform;
            }

            Transform found = prefab.transform.Find(objectPath);
            if (found == null)
            {
                // 루트 이름이 경로에 포함된 형태(Root/Child/...)도 허용
                int slash = objectPath.IndexOf('/');
                if (slash > 0 && objectPath.Substring(0, slash) == prefab.name)
                {
                    found = prefab.transform.Find(objectPath.Substring(slash + 1));
                }
            }

            return found;
        }

        /// <summary>대상 오브젝트에서 항목 종류에 맞는 컴포넌트를 찾습니다 (uGUI는 타입 이름으로 판정).</summary>
        private static Component FindTargetComponent(Transform target, AssetListItem item)
        {
            if (target == null)
            {
                return null;
            }

            string[] expected = ExpectedComponentNames(item);
            foreach (string name in expected)
            {
                foreach (Component component in target.GetComponents<Component>())
                {
                    if (component != null && component.GetType().Name == name)
                    {
                        return component;
                    }
                }
            }

            return null;
        }

        /// <summary>SerializedProperty로 오브젝트 참조 값을 할당합니다 (ApplyModifiedProperties가 Undo에 함께 기록됨).</summary>
        private static void SetObjectProperty(Component component, string propertyPath, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(component);
            SerializedProperty property = serialized.FindProperty(propertyPath);
            if (property == null)
            {
                throw new InvalidOperationException(
                    $"컴포넌트 {component.GetType().Name}에서 프로퍼티 \"{propertyPath}\"를 찾을 수 없습니다.");
            }

            property.objectReferenceValue = value;
            serialized.ApplyModifiedProperties();
        }

        /// <summary>SerializedProperty로 오브젝트 참조 값을 읽습니다.</summary>
        private static UnityEngine.Object GetObjectProperty(Component component, string propertyPath)
        {
            var serialized = new SerializedObject(component);
            SerializedProperty property = serialized.FindProperty(propertyPath);
            return property != null ? property.objectReferenceValue : null;
        }
    }
}
