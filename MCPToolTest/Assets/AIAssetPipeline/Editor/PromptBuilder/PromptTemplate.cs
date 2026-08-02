using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIAssetPipeline.Editor
{
    /// <summary>
    /// 2단계 프롬프트 제작에 쓰는 스타일 규칙 템플릿입니다.
    /// 기본 템플릿은 코드에 내장되어 있고(<see cref="CreateDefault"/>), JSON 리소스를 두면
    /// <see cref="LoadByName"/>으로 추가 템플릿을 선택할 수 있습니다.
    /// 템플릿은 두 폴더에서 찾습니다 — ① 패키지 폴더(<see cref="TemplatesFolder"/>, 배포 기본값·읽기 전용),
    /// ② 사용자 폴더(<see cref="UserTemplatesFolder"/>, 추가/오버라이드). 이름이 겹치면 사용자 폴더가 우선합니다.
    /// </summary>
    [Serializable]
    public class PromptTemplate
    {
        /// <summary>
        /// 패키지 동봉 템플릿 폴더 경로입니다 (에셋 경로 기준, 배포 기본값).
        /// <see cref="AIAssetPipelineSettings.InstallRoot"/> 기준으로 계산하므로 AIAssetPipeline 폴더 위치와 무관하게 동작합니다.
        /// UPM 패키지 설치 시 이 폴더는 읽기 전용(Packages/)이므로 사용자가 템플릿을 추가하려면
        /// <see cref="UserTemplatesFolder"/>를 사용해야 합니다.
        /// </summary>
        public static string TemplatesFolder
        {
            get { return AIAssetPipelineSettings.InstallRoot + "/Editor/PromptBuilder/Templates"; }
        }

        /// <summary>
        /// 사용자 템플릿 폴더 경로입니다 (고정: "Assets/AIAssetPipeline.User/Templates").
        /// 항상 프로젝트의 Assets 아래에 있어 UPM(읽기 전용 패키지) 설치에서도 자유롭게
        /// 템플릿을 추가·수정할 수 있으며, 패키지 폴더와 이름이 겹치면 이 폴더의 템플릿이 우선합니다.
        /// </summary>
        public const string UserTemplatesFolder = "Assets/AIAssetPipeline.User/Templates";

        /// <summary>기본 템플릿 이름입니다.</summary>
        public const string DefaultName = "default";

        /// <summary>템플릿 이름.</summary>
        public string templateName = DefaultName;

        /// <summary>이미지 공통 스타일 접두어 (positive 프롬프트 맨 앞).</summary>
        public string stylePrefix = "game asset illustration, fantasy game art style, vibrant colors";

        /// <summary>이미지 품질 태그 (positive 프롬프트 맨 뒤).</summary>
        public string qualityTags = "masterpiece, best quality, highly detailed, sharp focus";

        /// <summary>이미지 공통 negative 프롬프트.</summary>
        public string commonNegative =
            "text, watermark, signature, logo, blurry, lowres, jpeg artifacts, worst quality, deformed, cropped";

        /// <summary>UI 항목(isUI 또는 assetType=="ui")에 추가하는 UI 특화 태그.</summary>
        public string uiExtraTags = "clean edges, transparent background, game ui icon, centered composition, flat design";

        /// <summary>오디오 항목용 스타일 접두어 (이미지 태그를 쓰지 않음).</summary>
        public string audioStylePrefix = "game sound effect, clean studio recording";

        /// <summary>오디오 항목용 negative 프롬프트.</summary>
        public string audioNegative = "background noise, distortion, clipping, muffled";

        /// <summary>기본 템플릿을 생성합니다.</summary>
        public static PromptTemplate CreateDefault()
        {
            return new PromptTemplate();
        }

        /// <summary>
        /// 이름으로 템플릿을 로드합니다. 사용자 폴더(<see cref="UserTemplatesFolder"/>)를 먼저 찾고,
        /// 없으면 패키지 폴더(<see cref="TemplatesFolder"/>)를 찾습니다.
        /// 이름이 비어 있거나 "default"거나 해당 JSON이 어디에도 없으면 기본 템플릿을 반환합니다.
        /// </summary>
        /// <param name="name">템플릿 이름 (템플릿 폴더의 파일명, 확장자 제외).</param>
        public static PromptTemplate LoadByName(string name)
        {
            if (string.IsNullOrEmpty(name) || name == DefaultName)
            {
                return CreateDefault();
            }

            // 사용자 폴더 우선 → 패키지 폴더 → 기본 템플릿 폴백 (같은 이름이면 사용자 쪽이 이김).
            string path = $"{UserTemplatesFolder}/{name}.json";
            if (!File.Exists(path))
            {
                path = $"{TemplatesFolder}/{name}.json";
            }

            if (!File.Exists(path))
            {
                return CreateDefault();
            }

            var dict = MiniJson.Deserialize(File.ReadAllText(path)) as Dictionary<string, object>;
            if (dict == null)
            {
                return CreateDefault();
            }

            var template = CreateDefault();
            template.templateName = name;
            template.stylePrefix = GetString(dict, "stylePrefix", template.stylePrefix);
            template.qualityTags = GetString(dict, "qualityTags", template.qualityTags);
            template.commonNegative = GetString(dict, "commonNegative", template.commonNegative);
            template.uiExtraTags = GetString(dict, "uiExtraTags", template.uiExtraTags);
            template.audioStylePrefix = GetString(dict, "audioStylePrefix", template.audioStylePrefix);
            template.audioNegative = GetString(dict, "audioNegative", template.audioNegative);
            return template;
        }

        /// <summary>
        /// 사용 가능한 템플릿 이름 목록입니다
        /// ("default" + 패키지 폴더와 사용자 폴더의 JSON 파일명 병합, 이름 중복 시 1개만·정렬 유지).
        /// </summary>
        public static List<string> ListTemplateNames()
        {
            var merged = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string folder in new[] { TemplatesFolder, UserTemplatesFolder })
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                foreach (string file in Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrEmpty(name) && name != DefaultName)
                    {
                        merged.Add(name);
                    }
                }
            }

            var names = new List<string> { DefaultName };
            names.AddRange(merged);
            return names;
        }

        /// <summary>MiniJson 직렬화용 딕셔너리로 변환합니다 (MCP scan 반환용).</summary>
        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "templateName", templateName },
                { "stylePrefix", stylePrefix },
                { "qualityTags", qualityTags },
                { "commonNegative", commonNegative },
                { "uiExtraTags", uiExtraTags },
                { "audioStylePrefix", audioStylePrefix },
                { "audioNegative", audioNegative }
            };
        }

        private static string GetString(Dictionary<string, object> dict, string key, string fallback)
        {
            return dict.TryGetValue(key, out object v) && v is string s && !string.IsNullOrEmpty(s) ? s : fallback;
        }
    }
}
