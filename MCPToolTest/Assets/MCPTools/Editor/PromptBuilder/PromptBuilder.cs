using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace MCPTools.Editor
{
    /// <summary>
    /// 1단계 에셋 목록(<see cref="AssetListDocument"/>)을 템플릿 규칙으로 변환해
    /// 2단계 프롬프트 문서(<see cref="PromptSetDocument"/>) 초안을 만들고 저장하는 유틸리티입니다.
    /// </summary>
    public static class PromptBuilder
    {
        /// <summary>
        /// 에셋 목록을 템플릿 기반 프롬프트 문서 초안으로 변환합니다.
        /// UI 항목(isUI 또는 assetType=="ui")에는 UI 특화 태그가 자동 부여되고,
        /// 오디오 항목은 이미지 태그 없이 사운드 성격의 프롬프트로 만듭니다.
        /// </summary>
        /// <param name="list">1단계 에셋 목록 문서.</param>
        /// <param name="template">프롬프트 템플릿. null이면 기본 템플릿을 사용합니다.</param>
        /// <returns>생성된 프롬프트 문서 초안.</returns>
        /// <exception cref="ArgumentNullException">list가 null인 경우.</exception>
        public static PromptSetDocument Build(AssetListDocument list, PromptTemplate template)
        {
            if (list == null)
            {
                throw new ArgumentNullException(nameof(list));
            }

            if (template == null)
            {
                template = PromptTemplate.CreateDefault();
            }

            // assetListPath(목록 JSON 경로)는 문서 자신이 알 수 없으므로 창/도구에서 채운다.
            var doc = new PromptSetDocument
            {
                templateName = template.templateName,
                createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };

            foreach (AssetListItem source in list.items)
            {
                var item = new PromptItem
                {
                    id = source.id,
                    name = source.name,
                    assetType = source.assetType,
                    isUI = source.IsUI,
                    targetPrefabPath = source.targetPrefabPath,
                    targetObjectPath = source.targetObjectPath,
                    description = source.description
                };

                if (source.assetType == "audio")
                {
                    // 오디오는 이미지 태그를 붙이지 않고 사운드 성격으로 구성한다.
                    item.positive = Join(template.audioStylePrefix, Subject(source));
                    item.negative = template.audioNegative;
                }
                else
                {
                    bool isUiItem = source.IsUI || source.assetType == "ui";
                    item.positive = Join(
                        template.stylePrefix,
                        Subject(source),
                        isUiItem ? template.uiExtraTags : null,
                        template.qualityTags);
                    item.negative = template.commonNegative;
                }

                doc.items.Add(item);
            }

            return doc;
        }

        /// <summary>
        /// 1단계 에셋 목록 JSON 파일을 로드합니다.
        /// </summary>
        /// <param name="assetListPath">목록 JSON 경로 (Assets/ 기준 상대 경로).</param>
        /// <returns>로드된 문서.</returns>
        /// <exception cref="FileNotFoundException">파일이 존재하지 않는 경우.</exception>
        /// <exception cref="FormatException">JSON을 목록 문서로 해석할 수 없는 경우.</exception>
        public static AssetListDocument LoadAssetList(string assetListPath)
        {
            if (string.IsNullOrEmpty(assetListPath) || !File.Exists(assetListPath))
            {
                throw new FileNotFoundException(
                    $"에셋 목록 JSON 파일을 찾을 수 없습니다: {assetListPath}\n" +
                    "1단계(Tools/MCP/1. Asset Listup)에서 목록을 먼저 저장해주세요.");
            }

            var dict = MiniJson.Deserialize(File.ReadAllText(assetListPath)) as Dictionary<string, object>;
            AssetListDocument doc = AssetListDocument.FromDictionary(dict);
            if (doc == null || doc.items.Count == 0)
            {
                throw new FormatException(
                    $"에셋 목록 JSON을 해석할 수 없거나 항목이 없습니다: {assetListPath}\n" +
                    "1단계 저장 산출물(AssetList_*.json)인지 확인해주세요.");
            }

            return doc;
        }

        /// <summary>
        /// 문서를 검증합니다. 빈 프롬프트/이름 등의 경고 목록을 반환합니다 (저장은 차단하지 않음).
        /// </summary>
        /// <param name="doc">검증할 문서.</param>
        /// <returns>경고 목록 (비어 있으면 문제 없음).</returns>
        public static List<string> Validate(PromptSetDocument doc)
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
                PromptItem item = doc.items[i];
                string label = string.IsNullOrEmpty(item.name) ? $"(이름 없음 #{i + 1})" : item.name;

                if (string.IsNullOrEmpty(item.name))
                {
                    warnings.Add($"{label}: 이름이 비어 있습니다.");
                }

                if (string.IsNullOrEmpty(item.positive))
                {
                    warnings.Add($"{label}: positive 프롬프트가 비어 있습니다.");
                }

                if (string.IsNullOrEmpty(item.targetPrefabPath))
                {
                    warnings.Add($"{label}: 적용 대상 프리팹 경로가 지정되지 않았습니다.");
                }
            }

            return warnings;
        }

        /// <summary>
        /// 문서를 JSON으로 저장합니다. 폴더가 없으면 생성하고 저장 후 AssetDatabase에 반영합니다.
        /// </summary>
        /// <param name="doc">저장할 문서.</param>
        /// <param name="outputPath">저장 경로 (Assets/ 기준 상대 경로). null이면 설정의 Docs 경로에 자동 이름으로 저장.</param>
        /// <returns>실제 저장된 경로 (Assets/ 기준 상대 경로).</returns>
        public static string Save(PromptSetDocument doc, string outputPath = null)
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                MCPToolSettings settings = MCPToolSettings.GetOrCreate();
                outputPath = $"{settings.docsRootPath}/PromptSet_{DateTime.Now:yyyyMMdd_HHmm}.json";
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

        private static string Subject(AssetListItem source)
        {
            return string.IsNullOrEmpty(source.description)
                ? source.name
                : $"{source.name}, {source.description}";
        }

        private static string Join(params string[] parts)
        {
            var pieces = new List<string>();
            foreach (string part in parts)
            {
                if (!string.IsNullOrEmpty(part))
                {
                    pieces.Add(part.Trim());
                }
            }

            return string.Join(", ", pieces);
        }
    }
}
