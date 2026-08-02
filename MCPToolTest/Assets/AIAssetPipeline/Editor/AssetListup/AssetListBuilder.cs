using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace AIAssetPipeline.Editor
{
    /// <summary>
    /// 기획서 텍스트(md/txt)와 프로젝트 스캔 결과를 병합해 <see cref="AssetListDocument"/>를 만듭니다.
    /// 기획서 파싱은 헤딩/표/목록 기반 휴리스틱이며, 스캔 슬롯과 이름 유사 매칭을 시도합니다.
    /// </summary>
    public static class AssetListBuilder
    {
        // 이 키워드가 헤딩에 포함된 섹션에서만 에셋 후보를 추출한다 (기획서 전체 오염 방지).
        private static readonly string[] SectionKeywords =
        {
            "UI", "화면", "에셋", "아트", "이미지", "스프라이트", "사운드", "음향",
            "캐릭터", "적", "보스", "이펙트", "리소스", "튜토리얼"
        };

        private static readonly string[] AudioKeywords = { "사운드", "음향", "효과음", "bgm", "audio", "sfx" };
        private static readonly string[] UiKeywords = { "ui", "hud", "화면", "버튼", "패널", "바", "게이지", "아이콘", "타이머", "메뉴" };

        /// <summary>
        /// 기획서 파일과 스캔 결과로 에셋 목록 문서를 생성합니다.
        /// </summary>
        /// <param name="designDocPath">기획서 경로 (Assets/ 기준 상대 경로, .md/.txt).</param>
        /// <param name="scanEntries">프로젝트 스캔 결과 (null 허용).</param>
        /// <param name="scanRootPath">스캔 루트 경로 (문서에 기록용).</param>
        /// <returns>생성된 문서. 기획서 파일이 없으면 예외를 던집니다.</returns>
        /// <exception cref="FileNotFoundException">기획서 파일이 존재하지 않는 경우.</exception>
        public static AssetListDocument Build(string designDocPath, List<ScanEntry> scanEntries, string scanRootPath)
        {
            if (string.IsNullOrEmpty(designDocPath) || !File.Exists(designDocPath))
            {
                throw new FileNotFoundException($"기획서 파일을 찾을 수 없습니다: {designDocPath}");
            }

            var doc = new AssetListDocument
            {
                designDocPath = designDocPath.Replace('\\', '/'),
                scanRootPath = string.IsNullOrEmpty(scanRootPath) ? "Assets" : scanRootPath,
                createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };

            List<AssetListItem> docItems = ParseDesignDoc(File.ReadAllLines(designDocPath));
            var usedEntries = new HashSet<ScanEntry>();

            // 기획서 항목 ↔ 스캔 슬롯 이름 유사 매칭
            foreach (AssetListItem item in docItems)
            {
                ScanEntry match = FindMatch(item, scanEntries, usedEntries);
                if (match != null)
                {
                    usedEntries.Add(match);
                    item.targetPrefabPath = match.prefabPath;
                    item.targetScenePath = match.scenePath;
                    item.targetObjectPath = match.objectPath;
                    item.uiFlag = match.isUI ? 1 : 0;
                    if (match.componentType == "AudioSource")
                    {
                        item.assetType = "audio";
                    }

                    // 커스텀 스크립트 필드 슬롯과 매칭된 경우, 적용이 해당 직렬화 필드로 가도록 함께 기록한다.
                    if (match.IsFieldSlot)
                    {
                        item.targetComponent = match.componentType;
                        item.targetField = match.fieldPath;
                        if (match.fieldAssetType == "AudioClip")
                        {
                            item.assetType = "audio";
                        }
                    }
                }

                doc.items.Add(item);
            }

            // 매칭되지 않은 스캔 슬롯도 항목으로 추가 (대상 정보는 채워진 상태)
            if (scanEntries != null)
            {
                foreach (ScanEntry entry in scanEntries)
                {
                    if (usedEntries.Contains(entry))
                    {
                        continue;
                    }

                    doc.items.Add(ItemFromScanEntry(entry));
                }
            }

            AssignIds(doc.items);
            return doc;
        }

        /// <summary>
        /// 기획서에서 항목을 추출하지 않고 스캔 결과만으로 에셋 목록 문서를 생성합니다.
        /// 모든 스캔 슬롯(Image/RawImage/SpriteRenderer/AudioSource)이 대상 경로가 채워진 항목이 되며,
        /// 이름은 오브젝트 계층 경로의 말단 이름에서 파생되고 프롬프트용 설명은 후속 단계에서 보완합니다.
        /// 기획서 경로를 넘기면 항목 추출 없이 문서 컨텍스트(designDocPath)로만 기록되어
        /// 2단계(Prompt Builder)에서 참고할 수 있습니다.
        /// </summary>
        /// <param name="scanEntries">프리팹/씬 스캔 결과 (null이면 빈 문서).</param>
        /// <param name="scanRootPath">스캔 루트 경로 (문서에 기록용).</param>
        /// <param name="designDocPath">기획서 경로 (선택, 컨텍스트 기록용 — 항목 추출에는 사용하지 않음).</param>
        /// <returns>생성된 문서.</returns>
        public static AssetListDocument BuildFromScan(
            List<ScanEntry> scanEntries, string scanRootPath, string designDocPath = null)
        {
            var doc = new AssetListDocument
            {
                designDocPath = string.IsNullOrEmpty(designDocPath)
                    ? string.Empty
                    : designDocPath.Replace('\\', '/'),
                scanRootPath = string.IsNullOrEmpty(scanRootPath) ? "Assets" : scanRootPath,
                createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };

            if (scanEntries != null)
            {
                foreach (ScanEntry entry in scanEntries)
                {
                    doc.items.Add(ItemFromScanEntry(entry));
                }
            }

            AssignIds(doc.items);
            return doc;
        }

        /// <summary>
        /// 스캔 슬롯 1개를 대상 정보가 채워진 목록 항목으로 변환합니다.
        /// 커스텀 스크립트 필드 슬롯은 이름을 "스크립트명.필드명[i]" 말단 형태로 만들고
        /// targetComponent/targetField를 함께 기록합니다 (Sprite 필드 → "image"/UI 아님, AudioClip 필드 → "audio").
        /// </summary>
        private static AssetListItem ItemFromScanEntry(ScanEntry entry)
        {
            string currentSuffix = string.IsNullOrEmpty(entry.currentAssetName)
                ? " (현재 비어 있음)"
                : $" (현재: {entry.currentAssetName})";

            if (entry.IsFieldSlot)
            {
                return new AssetListItem
                {
                    name = entry.FieldDisplayName,
                    description = $"스캔됨: {entry.componentType} 스크립트 필드" + currentSuffix,
                    assetType = entry.fieldAssetType == "AudioClip" ? "audio" : "image",
                    targetPrefabPath = entry.prefabPath,
                    targetScenePath = entry.scenePath,
                    targetObjectPath = entry.objectPath,
                    targetComponent = entry.componentType,
                    targetField = entry.fieldPath,
                    uiFlag = 0
                };
            }

            return new AssetListItem
            {
                name = LeafName(entry.objectPath),
                description = $"스캔됨: {entry.componentType} 슬롯" + currentSuffix,
                assetType = entry.componentType == "AudioSource" ? "audio" : (entry.isUI ? "ui" : "image"),
                targetPrefabPath = entry.prefabPath,
                targetScenePath = entry.scenePath,
                targetObjectPath = entry.objectPath,
                uiFlag = entry.isUI ? 1 : 0
            };
        }

        /// <summary>
        /// 문서를 검증합니다. targetPrefabPath 또는 UI 여부가 미기록된 항목, 그리고
        /// 3단계에서 같은 파일명이 되어 서로 덮어쓰게 될 항목 id가 있으면 경고 목록을 반환합니다.
        /// </summary>
        /// <param name="doc">검증할 문서.</param>
        /// <returns>경고 목록 (비어 있으면 저장 가능).</returns>
        public static List<string> Validate(AssetListDocument doc)
        {
            var warnings = new List<string>();
            if (doc == null)
            {
                warnings.Add("문서가 비어 있습니다.");
                return warnings;
            }

            if (doc.items.Count == 0)
            {
                warnings.Add("항목이 하나도 없습니다.");
            }

            for (int i = 0; i < doc.items.Count; i++)
            {
                AssetListItem item = doc.items[i];
                string label = string.IsNullOrEmpty(item.name) ? $"(이름 없음 #{i + 1})" : item.name;

                if (string.IsNullOrEmpty(item.name))
                {
                    warnings.Add($"{label}: 이름이 비어 있습니다.");
                }

                if (string.IsNullOrEmpty(item.targetPrefabPath) && string.IsNullOrEmpty(item.targetScenePath))
                {
                    warnings.Add($"{label}: 적용 대상(프리팹 또는 씬) 경로가 지정되지 않았습니다.");
                }
                else if (!string.IsNullOrEmpty(item.targetPrefabPath) && !string.IsNullOrEmpty(item.targetScenePath))
                {
                    warnings.Add($"{label}: targetPrefabPath와 targetScenePath는 상호 배타입니다. 하나만 지정해주세요.");
                }

                if (!item.IsUISpecified)
                {
                    warnings.Add($"{label}: UI 여부가 지정되지 않았습니다.");
                }
            }

            // 항목 id는 3단계에서 후보 폴더명·확정본 파일명이 된다. 파일명에 쓸 수 없는 문자가 '_'로 바뀌면서
            // 서로 다른 id가 같은 이름이 되면 후보·확정본이 조용히 덮어써지므로, 저장은 막지 않고 경고만 남긴다.
            warnings.AddRange(FindIdCollisionWarnings(doc.items));

            return warnings;
        }

        /// <summary>
        /// 3단계 파일명으로 바꿨을 때 서로 충돌하는 항목 id 그룹을 찾아 경고 문구를 만듭니다.
        /// 파일명 변환 규칙은 실제 저장에 쓰는 규칙(<see cref="CandidateGenerator.PreviewFileNameForId"/>)을
        /// 그대로 사용해 판정이 어긋나지 않게 합니다.
        /// </summary>
        /// <param name="items">검사할 항목 목록.</param>
        /// <returns>충돌 경고 문구 목록. 충돌이 없으면 빈 목록.</returns>
        /// <remarks>
        /// <see cref="Validate"/>가 돌려주는 전체 경고에는 "대상 프리팹 미기록" 같은 흔한 항목이 섞여 있어
        /// 창에서 그대로 띄우면 소음이 된다. 창은 이 메서드로 <b>충돌 경고만</b> 뽑아 안내한다.
        /// </remarks>
        internal static List<string> FindIdCollisionWarnings(List<AssetListItem> items)
        {
            var warnings = new List<string>();
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var order = new List<string>(); // 경고 순서를 항목 순서로 고정한다.

            foreach (AssetListItem item in items)
            {
                // 빈 id는 저장 직전에 자동 부여되므로(item_001 …) 여기서는 검사 대상이 아니다.
                if (item == null || string.IsNullOrEmpty(item.id))
                {
                    continue;
                }

                string fileName = CandidateGenerator.PreviewFileNameForId(item.id);
                List<string> ids;
                if (!groups.TryGetValue(fileName, out ids))
                {
                    ids = new List<string>();
                    groups[fileName] = ids;
                    order.Add(fileName);
                }

                ids.Add(item.id);
            }

            foreach (string fileName in order)
            {
                List<string> ids = groups[fileName];
                if (ids.Count < 2)
                {
                    continue;
                }

                var distinct = new List<string>();
                foreach (string id in ids)
                {
                    if (!distinct.Contains(id))
                    {
                        distinct.Add(id);
                    }
                }

                string idList = "\"" + string.Join("\", \"", distinct.ToArray()) + "\"";
                warnings.Add(distinct.Count >= 2
                    ? $"항목 id {idList}이(가) 같은 파일명(\"{fileName}\")으로 저장됩니다. " +
                      "3단계 후보·확정본이 서로 덮어써지므로 id를 파일명에 쓸 수 있는 " +
                      "문자(영문·숫자·_·-)로 구분해주세요."
                    : $"항목 id {idList}이(가) {ids.Count}번 중복 사용됐습니다. " +
                      "3단계 후보·확정본이 서로 덮어써지므로 항목마다 다른 id를 지정해주세요.");
            }

            return warnings;
        }

        /// <summary>
        /// 저장해 둔 목록 JSON을 불러옵니다. 이어서 편집(항목 추가/수정/삭제)한 뒤
        /// 같은 경로로 <see cref="Save"/>하면 덮어쓸 수 있습니다.
        /// </summary>
        /// <param name="assetListPath">목록 JSON 경로 (Assets/ 기준 상대 경로).</param>
        /// <returns>로드된 문서.</returns>
        /// <exception cref="FileNotFoundException">파일이 존재하지 않는 경우.</exception>
        /// <exception cref="FormatException">JSON을 목록 문서로 해석할 수 없는 경우.</exception>
        public static AssetListDocument Load(string assetListPath)
        {
            if (string.IsNullOrEmpty(assetListPath) || !File.Exists(assetListPath))
            {
                throw new FileNotFoundException(
                    $"목록 JSON 파일을 찾을 수 없습니다: {assetListPath}");
            }

            var dict = MiniJson.Deserialize(File.ReadAllText(assetListPath)) as Dictionary<string, object>;
            AssetListDocument doc = AssetListDocument.FromDictionary(dict);
            if (doc == null)
            {
                throw new FormatException(
                    $"목록 JSON을 해석할 수 없습니다: {assetListPath}\n" +
                    "1단계에서 저장한 AssetList_*.json인지 확인해주세요.");
            }

            // 항목이 0개인 문서는 오류가 아니다 (빈 목록을 저장했다가 다시 채우는 흐름을 허용).
            return doc;
        }

        /// <summary>
        /// 문서를 JSON으로 저장합니다. 폴더가 없으면 생성하고 저장 후 AssetDatabase에 반영합니다.
        /// </summary>
        /// <param name="doc">저장할 문서.</param>
        /// <param name="outputPath">저장 경로 (Assets/ 기준 상대 경로). null이면 설정의 Docs 경로에 자동 이름으로 저장.</param>
        /// <returns>실제 저장된 경로 (Assets/ 기준 상대 경로).</returns>
        public static string Save(AssetListDocument doc, string outputPath = null)
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                AIAssetPipelineSettings settings = AIAssetPipelineSettings.GetOrCreate();
                outputPath = $"{AIAssetPipelineFolders.AssetListDir(settings)}/AssetList_{DateTime.Now:yyyyMMdd_HHmm}.json";
            }

            outputPath = outputPath.Replace('\\', '/');
            string directory = Path.GetDirectoryName(outputPath);
            bool createdFolder = false;
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                createdFolder = true;
            }

            File.WriteAllText(outputPath, MiniJson.Serialize(doc.ToDictionary()));

            // 폴더를 새로 만든 경우에는 AssetDatabase가 그 폴더 자체를 모르므로 Refresh로 함께 반영한다.
            // 폴더가 이미 있으면 대상 파일만 임포트한다 (전체 Refresh는 느리다).
            if (createdFolder)
            {
                AssetDatabase.Refresh();
            }
            else
            {
                AssetDatabase.ImportAsset(outputPath);
            }

            return outputPath;
        }

        private static List<AssetListItem> ParseDesignDoc(string[] lines)
        {
            var items = new List<AssetListItem>();
            var seenNames = new HashSet<string>();
            bool inRelevantSection = false;
            string sectionHeading = string.Empty;
            int tableRowIndex = -1; // -1: 표 밖, 0: 헤더 행

            foreach (string raw in lines)
            {
                string line = raw.Trim();

                if (line.StartsWith("#"))
                {
                    sectionHeading = line.TrimStart('#').Trim();
                    inRelevantSection = ContainsAny(sectionHeading, SectionKeywords);
                    tableRowIndex = -1;
                    continue;
                }

                if (!inRelevantSection)
                {
                    continue;
                }

                if (line.StartsWith("|"))
                {
                    // 구분선(|---|---|)은 건너뛴다
                    if (line.Replace("|", string.Empty).Replace("-", string.Empty)
                            .Replace(":", string.Empty).Trim().Length == 0)
                    {
                        continue;
                    }

                    tableRowIndex++;
                    if (tableRowIndex == 0)
                    {
                        continue; // 헤더 행
                    }

                    string[] cells = SplitTableRow(line);
                    if (cells.Length >= 1 && !string.IsNullOrEmpty(cells[0]))
                    {
                        AddItem(items, seenNames, cells[0],
                            cells.Length >= 2 ? cells[1] : string.Empty, sectionHeading);
                    }

                    continue;
                }

                tableRowIndex = -1;

                // 목록 항목 "- 이름 : 설명" 형태만 후보로 채택 (일반 서술 불릿 제외)
                if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    string body = line.Substring(2).Trim();
                    int sep = body.IndexOf(':');
                    if (sep > 0 && sep <= 24)
                    {
                        AddItem(items, seenNames, body.Substring(0, sep).Trim(),
                            body.Substring(sep + 1).Trim(), sectionHeading);
                    }
                }
            }

            return items;
        }

        private static void AddItem(
            List<AssetListItem> items, HashSet<string> seenNames, string name, string description, string section)
        {
            name = name.Trim();
            if (name.Length == 0 || name.Length > 24)
            {
                return;
            }

            string key = Normalize(name);
            if (key.Length == 0 || !seenNames.Add(key))
            {
                return;
            }

            string context = section + " " + name;
            string assetType = ContainsAny(context, AudioKeywords) ? "audio"
                : ContainsAny(context, UiKeywords) ? "ui" : "image";

            items.Add(new AssetListItem
            {
                name = name,
                description = string.IsNullOrEmpty(description) ? $"기획서 섹션 \"{section}\"에서 추출" : description,
                assetType = assetType,
                uiFlag = assetType == "ui" ? 1 : -1
            });
        }

        private static ScanEntry FindMatch(
            AssetListItem item, List<ScanEntry> entries, HashSet<ScanEntry> usedEntries)
        {
            if (entries == null)
            {
                return null;
            }

            string key = Normalize(item.name);
            if (key.Length == 0)
            {
                return null;
            }

            foreach (ScanEntry entry in entries)
            {
                if (usedEntries.Contains(entry))
                {
                    continue;
                }

                string objectName = Normalize(LeafName(entry.objectPath));
                string assetName = Normalize(entry.currentAssetName);
                if ((objectName.Length > 0 && (objectName.Contains(key) || key.Contains(objectName))) ||
                    (assetName.Length > 0 && (assetName.Contains(key) || key.Contains(assetName))))
                {
                    return entry;
                }
            }

            return null;
        }

        private static void AssignIds(List<AssetListItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (string.IsNullOrEmpty(items[i].id))
                {
                    items[i].id = $"item_{i + 1:000}";
                }
            }
        }

        private static string[] SplitTableRow(string line)
        {
            string[] parts = line.Trim().Trim('|').Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Trim();
            }

            return parts;
        }

        private static bool ContainsAny(string text, string[] keywords)
        {
            string lower = text.ToLowerInvariant();
            foreach (string keyword in keywords)
            {
                if (lower.Contains(keyword.ToLowerInvariant()))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Normalize(string text)
        {
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : text.ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty);
        }

        private static string LeafName(string objectPath)
        {
            if (string.IsNullOrEmpty(objectPath))
            {
                return string.Empty;
            }

            int slash = objectPath.LastIndexOf('/');
            return slash >= 0 ? objectPath.Substring(slash + 1) : objectPath;
        }
    }
}
