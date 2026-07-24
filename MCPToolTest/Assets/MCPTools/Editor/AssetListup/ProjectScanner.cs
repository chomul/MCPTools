using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPTools.Editor
{
    /// <summary>
    /// 프리팹/씬 스캔으로 찾은 에셋 적용 슬롯 1개입니다.
    /// </summary>
    [Serializable]
    public class ScanEntry
    {
        /// <summary>프리팹 경로 (Assets/ 기준 상대 경로). 씬 슬롯이면 빈 문자열.</summary>
        public string prefabPath = string.Empty;

        /// <summary>씬 경로 (Assets/ 기준 상대 경로). 씬에 직접 배치된 오브젝트 슬롯이면 비어 있지 않음 (prefabPath와 상호 배타).</summary>
        public string scenePath = string.Empty;

        /// <summary>루트 기준 GameObject 계층 경로 (예: "Canvas/HpBar/Fill"). 씬 슬롯은 씬 루트 오브젝트부터의 경로.</summary>
        public string objectPath = string.Empty;

        /// <summary>컴포넌트 종류: "Image" / "RawImage" / "SpriteRenderer" / "AudioSource".</summary>
        public string componentType = string.Empty;

        /// <summary>현재 할당된 에셋 이름 (없으면 빈 문자열).</summary>
        public string currentAssetName = string.Empty;

        /// <summary>UI 컴포넌트(Image/RawImage) 여부입니다.</summary>
        public bool isUI;
    }

    /// <summary>
    /// 프로젝트의 프리팹을 스캔해 이미지·UI·오디오 슬롯 후보를 수집합니다.
    /// uGUI(<c>com.unity.ugui</c>) 어셈블리를 참조하지 않고 컴포넌트 타입 이름과 SerializedProperty로
    /// Image/RawImage를 다루므로, uGUI가 설치되지 않은 프로젝트에서도 컴파일·동작합니다
    /// (그런 프로젝트에서는 Image/RawImage 슬롯이 존재하지 않으므로 수집되지 않을 뿐입니다).
    /// </summary>
    public static class ProjectScanner
    {
        /// <summary>uGUI Image의 Sprite 참조 직렬화 필드 이름입니다.</summary>
        private const string ImageSpriteProperty = "m_Sprite";

        /// <summary>uGUI RawImage의 Texture 참조 직렬화 필드 이름입니다.</summary>
        private const string RawImageTextureProperty = "m_Texture";

        /// <summary>
        /// rootPath 아래의 모든 프리팹에서 Image/RawImage/SpriteRenderer/AudioSource 슬롯을 수집합니다.
        /// </summary>
        /// <param name="rootPath">스캔 루트 폴더 (Assets/ 기준 상대 경로, 예: "Assets").</param>
        /// <returns>발견한 슬롯 목록 (프리팹이 없으면 빈 목록).</returns>
        public static List<ScanEntry> ScanPrefabs(string rootPath)
        {
            var results = new List<ScanEntry>();
            if (string.IsNullOrEmpty(rootPath))
            {
                rootPath = "Assets";
            }

            if (!AssetDatabase.IsValidFolder(rootPath))
            {
                Debug.LogWarning($"[MCPTools] 스캔 루트 폴더가 없습니다: {rootPath}");
                return results;
            }

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { rootPath });
            foreach (string guid in guids)
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                {
                    continue;
                }

                CollectSlots(prefab, prefabPath, results);
            }

            return results;
        }

        /// <summary>
        /// 사용자가 지정한 씬들을 스캔해, 씬에 직접 배치된(프리팹 인스턴스가 아닌) 오브젝트의
        /// Image/RawImage/SpriteRenderer/AudioSource 슬롯을 수집합니다.
        /// 이미 열려 있는 씬은 그대로 사용하고, 열려 있지 않은 씬은 Additive로 열어 스캔 후 닫아
        /// 현재 열린 씬 상태를 복원합니다 (Additive 열기는 기존 씬을 파괴하지 않으며 스캔은 읽기 전용이므로
        /// 별도의 저장 확인 없이 안전합니다).
        /// 프리팹 인스턴스 소속 오브젝트는 원본 프리팹 스캔(<see cref="ScanPrefabs"/>)으로 커버되므로 제외합니다.
        /// </summary>
        /// <param name="scenePaths">스캔할 씬 경로 목록 (Assets/ 기준 상대 경로, 예: "Assets/Scenes/Main.unity").</param>
        /// <returns>발견한 슬롯 목록 (scenePath가 채워짐). 잘못된 경로는 경고 로그 후 건너뜁니다.</returns>
        public static List<ScanEntry> ScanScenes(List<string> scenePaths)
        {
            var results = new List<ScanEntry>();
            if (scenePaths == null || scenePaths.Count == 0)
            {
                return results;
            }

            var visited = new HashSet<string>();
            foreach (string rawPath in scenePaths)
            {
                if (string.IsNullOrEmpty(rawPath))
                {
                    continue;
                }

                string scenePath = rawPath.Replace('\\', '/');
                if (!visited.Add(scenePath))
                {
                    continue;
                }

                if (!System.IO.File.Exists(scenePath) || !scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning($"[MCPTools] 스캔할 씬 파일을 찾을 수 없습니다: {scenePath}");
                    continue;
                }

                Scene scene = SceneManager.GetSceneByPath(scenePath);
                bool openedHere = false;
                if (!scene.isLoaded)
                {
                    scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                    openedHere = true;
                }

                try
                {
                    CollectSceneSlots(scene, scenePath, results);
                }
                finally
                {
                    if (openedHere)
                    {
                        // 스캔은 읽기 전용이므로 저장 없이 닫아 원래 열린 씬 상태를 복원한다.
                        EditorSceneManager.CloseScene(scene, true);
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// 현재 에디터에 열려 있는(로드된) 모든 씬을 스캔합니다. 씬에 직접 배치된 오브젝트의 슬롯과,
        /// 씬에 포함된 프리팹 인스턴스가 참조하는 원본 프리팹의 슬롯을 함께 수집합니다
        /// (기획서 없이 스캔만으로 목록을 만드는 scanOnly 모드용).
        /// 프리팹 인스턴스 소속 오브젝트 자체는 원본 프리팹 스캔으로 커버되므로 씬 슬롯에서는 제외됩니다.
        /// </summary>
        /// <returns>발견한 슬롯 목록 (씬 슬롯은 scenePath, 프리팹 슬롯은 prefabPath가 채워짐).</returns>
        public static List<ScanEntry> ScanOpenScenesWithPrefabs()
        {
            var results = new List<ScanEntry>();
            var prefabPaths = new HashSet<string>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || string.IsNullOrEmpty(scene.path))
                {
                    continue; // 저장되지 않은 임시 씬은 경로가 없어 목록 대상에서 제외
                }

                CollectSceneSlots(scene, scene.path.Replace('\\', '/'), results);

                // 씬에 포함된 프리팹 인스턴스의 원본 프리팹 경로를 수집한다.
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (!PrefabUtility.IsAnyPrefabInstanceRoot(transform.gameObject))
                        {
                            continue;
                        }

                        string prefabPath =
                            PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject);
                        if (!string.IsNullOrEmpty(prefabPath))
                        {
                            prefabPaths.Add(prefabPath.Replace('\\', '/'));
                        }
                    }
                }
            }

            foreach (string prefabPath in prefabPaths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab != null)
                {
                    CollectSlots(prefab, prefabPath, results);
                }
            }

            return results;
        }

        /// <summary>
        /// 스캔 전용(scanOnly) 모드의 슬롯 수집입니다. <see cref="ScanOpenScenesWithPrefabs"/> 결과에
        /// rootPath 아래 모든 프리팹의 슬롯(<see cref="ScanPrefabs"/>)을 항상 병합하므로,
        /// 열려 있는 씬이 비어 있어도 프로젝트의 프리팹을 대상으로 목록을 만들 수 있습니다.
        /// 두 스캔에 같은 프리팹이 겹치면 프리팹 경로 단위로 한 번만 담습니다 (두 경로 모두 같은 수집 루틴을
        /// 쓰므로 프리팹이 같으면 슬롯 목록도 동일합니다. 슬롯 단위 키로 걸러내면 이름이 같은 형제
        /// 오브젝트의 슬롯까지 사라지므로 프리팹 경로 단위로 처리합니다).
        /// 씬에 직접 배치된 슬롯은 프리팹 스캔에 나타나지 않으므로 그대로 유지됩니다.
        /// </summary>
        /// <param name="rootPath">프리팹 스캔 루트 폴더 (Assets/ 기준 상대 경로, 비면 "Assets").</param>
        /// <returns>발견한 슬롯 목록 (씬 슬롯은 scenePath, 프리팹 슬롯은 prefabPath가 채워짐).</returns>
        public static List<ScanEntry> ScanOpenScenesAndPrefabs(string rootPath)
        {
            List<ScanEntry> results = ScanOpenScenesWithPrefabs();

            var scannedPrefabPaths = new HashSet<string>();
            foreach (ScanEntry entry in results)
            {
                if (!string.IsNullOrEmpty(entry.prefabPath))
                {
                    scannedPrefabPaths.Add(entry.prefabPath);
                }
            }

            foreach (ScanEntry entry in ScanPrefabs(rootPath))
            {
                if (scannedPrefabPaths.Contains(entry.prefabPath))
                {
                    continue; // 열린 씬 스캔에서 이미 수집한 프리팹
                }

                results.Add(entry);
            }

            return results;
        }

        private static void CollectSceneSlots(Scene scene, string scenePath, List<ScanEntry> results)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                CollectUISceneSlots(root, scenePath, "Image", ImageSpriteProperty, results);
                CollectUISceneSlots(root, scenePath, "RawImage", RawImageTextureProperty, results);
                CollectComponentSlots<SpriteRenderer>(root, scenePath, "SpriteRenderer", results,
                    renderer => renderer.sprite != null ? renderer.sprite.name : string.Empty, false);
                CollectComponentSlots<AudioSource>(root, scenePath, "AudioSource", results,
                    audio => audio.clip != null ? audio.clip.name : string.Empty, false);
            }
        }

        private static void CollectComponentSlots<T>(
            GameObject root, string scenePath, string componentType, List<ScanEntry> results,
            Func<T, string> currentAssetName, bool isUI) where T : Component
        {
            foreach (T component in root.GetComponentsInChildren<T>(true))
            {
                // 프리팹 인스턴스 소속 오브젝트는 원본 프리팹 스캔으로 커버되므로 제외한다.
                if (PrefabUtility.IsPartOfPrefabInstance(component.gameObject))
                {
                    continue;
                }

                results.Add(new ScanEntry
                {
                    scenePath = scenePath,
                    objectPath = GetHierarchyPath(component.transform, null),
                    componentType = componentType,
                    currentAssetName = currentAssetName(component),
                    isUI = isUI
                });
            }
        }

        /// <summary>
        /// 씬 오브젝트에서 uGUI 컴포넌트(Image/RawImage) 슬롯을 수집합니다.
        /// uGUI 어셈블리를 참조하지 않기 위해 타입 이름으로 컴포넌트를 판정하고,
        /// 현재 참조 중인 에셋 이름은 SerializedProperty로 읽습니다.
        /// </summary>
        private static void CollectUISceneSlots(
            GameObject root, string scenePath, string componentType, string propertyPath, List<ScanEntry> results)
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (!IsComponentOfType(component, componentType))
                {
                    continue;
                }

                // 프리팹 인스턴스 소속 오브젝트는 원본 프리팹 스캔으로 커버되므로 제외한다.
                if (PrefabUtility.IsPartOfPrefabInstance(component.gameObject))
                {
                    continue;
                }

                results.Add(new ScanEntry
                {
                    scenePath = scenePath,
                    objectPath = GetHierarchyPath(component.transform, null),
                    componentType = componentType,
                    currentAssetName = GetReferencedAssetName(component, propertyPath),
                    isUI = true
                });
            }
        }

        /// <summary>
        /// 프리팹에서 uGUI 컴포넌트(Image/RawImage) 슬롯을 수집합니다 (판정·값 조회 방식은 씬과 동일).
        /// </summary>
        private static void CollectUIPrefabSlots(
            GameObject prefab, string prefabPath, string componentType, string propertyPath, List<ScanEntry> results)
        {
            foreach (Component component in prefab.GetComponentsInChildren<Component>(true))
            {
                if (!IsComponentOfType(component, componentType))
                {
                    continue;
                }

                results.Add(MakeEntry(prefabPath, component.transform, prefab.transform, componentType,
                    GetReferencedAssetName(component, propertyPath), true));
            }
        }

        /// <summary>
        /// 컴포넌트가 지정한 이름의 타입이거나 그 파생 타입인지 판정합니다.
        /// (uGUI 어셈블리 참조 없이 타입 이름으로 판정하되, GetComponentsInChildren&lt;Image&gt;와 동일하게
        /// 사용자 정의 파생 클래스도 잡도록 기반 타입을 거슬러 올라갑니다.)
        /// </summary>
        private static bool IsComponentOfType(Component component, string typeName)
        {
            if (component == null)
            {
                return false; // 스크립트가 없는(Missing) 컴포넌트
            }

            for (Type type = component.GetType(); type != null; type = type.BaseType)
            {
                if (string.Equals(type.Name, typeName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// SerializedProperty로 컴포넌트가 참조 중인 에셋 이름을 읽습니다 (없으면 빈 문자열).
        /// </summary>
        private static string GetReferencedAssetName(Component component, string propertyPath)
        {
            var serialized = new SerializedObject(component);
            SerializedProperty property = serialized.FindProperty(propertyPath);
            UnityEngine.Object value = property != null ? property.objectReferenceValue : null;
            return value != null ? value.name : string.Empty;
        }

        private static void CollectSlots(GameObject prefab, string prefabPath, List<ScanEntry> results)
        {
            CollectUIPrefabSlots(prefab, prefabPath, "Image", ImageSpriteProperty, results);
            CollectUIPrefabSlots(prefab, prefabPath, "RawImage", RawImageTextureProperty, results);

            foreach (SpriteRenderer renderer in prefab.GetComponentsInChildren<SpriteRenderer>(true))
            {
                results.Add(MakeEntry(prefabPath, renderer.transform, prefab.transform, "SpriteRenderer",
                    renderer.sprite != null ? renderer.sprite.name : string.Empty, false));
            }

            foreach (AudioSource audio in prefab.GetComponentsInChildren<AudioSource>(true))
            {
                results.Add(MakeEntry(prefabPath, audio.transform, prefab.transform, "AudioSource",
                    audio.clip != null ? audio.clip.name : string.Empty, false));
            }
        }

        private static ScanEntry MakeEntry(
            string prefabPath, Transform target, Transform root, string componentType, string assetName, bool isUI)
        {
            return new ScanEntry
            {
                prefabPath = prefabPath,
                objectPath = GetHierarchyPath(target, root),
                componentType = componentType,
                currentAssetName = assetName,
                isUI = isUI
            };
        }

        /// <summary>root 기준 계층 경로를 만듭니다. root가 null이면 씬 루트 오브젝트까지의 전체 경로를 만듭니다.</summary>
        private static string GetHierarchyPath(Transform target, Transform root)
        {
            if (root != null && target == root)
            {
                return target.name;
            }

            var segments = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                segments.Add(current.name);
                current = current.parent;
            }

            if (root != null)
            {
                segments.Add(root.name);
            }

            segments.Reverse();
            return string.Join("/", segments);
        }
    }
}
