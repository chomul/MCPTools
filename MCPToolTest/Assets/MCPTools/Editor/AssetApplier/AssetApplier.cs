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
        /// <summary>
        /// 항목의 확정본 에셋 경로를 자동 탐색합니다.
        /// GenerationResults.json의 outputPath 기록을 우선 사용하고,
        /// 없으면 규칙 경로(Assets/Generated/Images/{id}.png, Audio/{id}.*)를 탐색합니다.
        /// </summary>
        /// <param name="settings">설정 객체.</param>
        /// <param name="item">대상 항목.</param>
        /// <returns>확정본 경로 (Assets/ 기준 상대 경로). 찾지 못하면 null.</returns>
        public static string FindConfirmedAssetPath(MCPToolSettings settings, AssetListItem item)
        {
            if (settings == null || item == null || string.IsNullOrEmpty(item.id))
            {
                return null;
            }

            string root = settings.generatedRootPath.TrimEnd('/');

            // 1) GenerationResults.json 기록 우선
            string resultsPath = $"{root}/{CandidateGenerator.ResultsFileName}";
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

            // 2) 규칙 경로 폴백
            if (item.assetType == "audio")
            {
                string audioFolder = $"{root}/Audio";
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
                string imagePath = $"{root}/Images/{item.id}.png";
                if (File.Exists(imagePath))
                {
                    return imagePath;
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

                    break;

                default: // Image / SpriteRenderer → Sprite 필요
                    if (AssetDatabase.LoadAssetAtPath<Sprite>(assetPath) == null)
                    {
                        reasons.Add(
                            $"에셋이 Sprite로 임포트되지 않았습니다: \"{assetPath}\". " +
                            "Texture Type을 Sprite (2D and UI)로 변경해주세요.");
                    }

                    break;
            }

            return reasons;
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
                PrefabUtility.SavePrefabAsset(prefab);

                result.success = true;
                result.message =
                    $"적용 완료: {item.targetPrefabPath} → {component.GetType().Name} ({result.appliedAssetPath})";
            }
            catch (Exception e)
            {
                result.message = $"적용 중 오류가 발생했습니다: {e.Message}";
            }

            return result;
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
        /// 컴포넌트 종류에 맞게 에셋을 할당합니다 (Undo.RecordObject 등록 포함, 프리팹/씬 공용).
        /// 오디오 임의 필드 대상(targetComponent+targetField)은 해당 직렬화 필드에 AudioClip을 할당합니다.
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
                    ((SpriteRenderer)component).sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                    break;

                case "RawImage":
                    // uGUI 어셈블리 참조 없이 SerializedProperty로 할당한다.
                    SetObjectProperty(component, "m_Texture", AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath));
                    break;

                default: // Image
                    SetObjectProperty(component, "m_Sprite", AssetDatabase.LoadAssetAtPath<Sprite>(assetPath));
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
