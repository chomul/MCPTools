using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// MCP Tools가 사용하는 프로젝트 작업 폴더(기획서 문서 루트, 생성 결과 루트)를 보장하는 헬퍼입니다.
    /// 도구를 처음 넣은 프로젝트에서 사용자가 폴더를 직접 만들지 않아도 되도록
    /// 각 단계 창이 열릴 때 호출합니다 (D12).
    /// </summary>
    internal static class MCPToolFolders
    {
        // ──────────────────── 단계별 하위 폴더 이름 ────────────────────
        // 산출물이 문서 루트/생성 루트에 평평하게 쌓이면 기획서와 섞이고 단계 구분이 되지 않아
        // 단계별 하위 폴더로 나눈다. 새로 저장하는 파일만 이 위치를 쓰고,
        // 읽기는 항상 구 위치(루트)까지 함께 훑어 기존 프로젝트가 깨지지 않게 한다.

        /// <summary>1단계 산출물(AssetList_*.json) 하위 폴더 이름입니다.</summary>
        internal const string AssetListFolder = "1_AssetList";

        /// <summary>2단계 산출물(PromptSet_*.json) 하위 폴더 이름입니다.</summary>
        internal const string PromptSetFolder = "2_PromptSet";

        /// <summary>
        /// 스프라이트 시트 프롬프트(SpriteSheetPrompt_*.json) 하위 폴더 이름입니다.
        /// 시트 이미지가 아니라 외부 AI에 넘길 프롬프트 문서가 들어가므로 Docs 아래에 두고,
        /// 다른 폴더와 마찬가지로 파일 접두사와 이름을 일치시킨다(시트 PNG는 확정본 폴더로 저장됨).
        /// </summary>
        internal const string SpriteSheetFolder = "SpriteSheetPrompt";

        /// <summary>3단계 후보 저장 하위 폴더 이름입니다 (구 위치: "Candidates").</summary>
        internal const string CandidatesFolder = "3_Candidates";

        /// <summary>3단계 확정본 하위 폴더 이름입니다 (구 위치: 생성 루트 바로 아래).</summary>
        internal const string ConfirmedFolder = "3_Confirmed";

        /// <summary>
        /// 슬라이스한 스프라이트 시트 이미지가 저장되는 확정본 하위 폴더 이름입니다.
        /// 시트는 4단계 적용 대상(항목별 확정본)이 아니라 슬라이스 입력이므로 Images/와 분리한다.
        /// </summary>
        internal const string SpriteSheetsFolder = "SpriteSheets";

        /// <summary>하위 폴더 도입 이전의 후보 폴더 이름입니다 (읽기 폴백 전용).</summary>
        internal const string LegacyCandidatesFolder = "Candidates";

        /// <summary>설정의 문서 루트를 정규화해 반환합니다 (빈 값이면 기본값).</summary>
        internal static string DocsRoot(MCPToolSettings settings)
        {
            string path = settings != null ? settings.docsRootPath : null;
            return (string.IsNullOrEmpty(path) ? "Assets/Docs" : path).Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>설정의 생성 결과 루트를 정규화해 반환합니다 (빈 값이면 기본값).</summary>
        internal static string GeneratedRoot(MCPToolSettings settings)
        {
            string path = settings != null ? settings.generatedRootPath : null;
            return (string.IsNullOrEmpty(path) ? "Assets/Generated" : path).Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>1단계 산출물을 저장할 폴더입니다.</summary>
        internal static string AssetListDir(MCPToolSettings settings)
        {
            return $"{DocsRoot(settings)}/{AssetListFolder}";
        }

        /// <summary>2단계 산출물을 저장할 폴더입니다.</summary>
        internal static string PromptSetDir(MCPToolSettings settings)
        {
            return $"{DocsRoot(settings)}/{PromptSetFolder}";
        }

        /// <summary>스프라이트 시트 프롬프트를 저장할 폴더입니다.</summary>
        internal static string SpriteSheetDir(MCPToolSettings settings)
        {
            return $"{DocsRoot(settings)}/{SpriteSheetFolder}";
        }

        /// <summary>3단계 후보들이 항목별 폴더로 놓이는 상위 폴더입니다.</summary>
        internal static string CandidatesRoot(MCPToolSettings settings)
        {
            return $"{GeneratedRoot(settings)}/{CandidatesFolder}";
        }

        /// <summary>3단계 확정본(Images/Audio/SpriteSheets + 결과 문서)이 놓이는 폴더입니다.</summary>
        internal static string ConfirmedRoot(MCPToolSettings settings)
        {
            return $"{GeneratedRoot(settings)}/{ConfirmedFolder}";
        }

        /// <summary>슬라이스한 스프라이트 시트 이미지가 저장될 폴더입니다.</summary>
        internal static string SpriteSheetsDir(MCPToolSettings settings)
        {
            return $"{ConfirmedRoot(settings)}/{SpriteSheetsFolder}";
        }

        /// <summary>
        /// 새 하위 폴더와 구 위치(루트)를 함께 훑어 패턴에 맞는 파일을 최신순으로 반환합니다.
        /// 같은 파일명이 양쪽에 있으면 새 위치를 채택합니다.
        /// </summary>
        /// <param name="root">문서 루트 또는 생성 루트 (Assets 기준 상대 경로).</param>
        /// <param name="subfolder">새 하위 폴더 이름.</param>
        /// <param name="searchPattern">파일 검색 패턴 (예: "AssetList_*.json").</param>
        /// <returns>최신 수정 순으로 정렬된 경로 배열 (Assets 기준, 구분자 '/'). 없으면 빈 배열.</returns>
        internal static string[] FindDocuments(string root, string subfolder, string searchPattern)
        {
            var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 구 위치를 먼저 담고 새 위치로 덮어써, 파일명이 겹치면 새 위치가 이긴다.
            CollectInto(byFileName, root, searchPattern);
            CollectInto(byFileName, $"{root}/{subfolder}", searchPattern);

            var paths = new List<string>(byFileName.Values);
            paths.Sort((a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
            return paths.ToArray();
        }

        private static void CollectInto(
            Dictionary<string, string> target, string folder, string searchPattern)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return;
            }

            foreach (string path in Directory.GetFiles(folder, searchPattern, SearchOption.TopDirectoryOnly))
            {
                target[Path.GetFileName(path)] = path.Replace('\\', '/');
            }
        }

        /// <summary>
        /// 새 위치의 파일이 있으면 그 경로를, 없고 구 위치에 있으면 구 위치 경로를 반환합니다.
        /// 둘 다 없으면 새 위치 경로를 반환합니다 (새로 만들 대상 경로).
        /// </summary>
        /// <param name="preferredPath">새 위치 경로.</param>
        /// <param name="legacyPath">구 위치 경로.</param>
        /// <returns>읽기에 사용할 경로.</returns>
        internal static string ResolveForRead(string preferredPath, string legacyPath)
        {
            if (!string.IsNullOrEmpty(preferredPath) && File.Exists(preferredPath))
            {
                return preferredPath;
            }

            if (!string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath))
            {
                return legacyPath;
            }

            return preferredPath;
        }

        /// <summary>
        /// 설정의 문서 루트(<c>docsRootPath</c>)와 생성 결과 루트(<c>generatedRootPath</c>) 폴더,
        /// 그리고 단계별 하위 폴더를 보장합니다.
        /// 이미 있으면 아무 것도 하지 않고, 새로 만든 폴더가 있을 때만 콘솔에 1회 안내합니다.
        /// </summary>
        /// <param name="settings">경로를 읽을 설정 객체. null이면 아무 것도 하지 않습니다.</param>
        internal static void EnsureWorkFolders(MCPToolSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            var created = new List<string>();
            foreach (string folder in new[]
                     {
                         settings.docsRootPath,
                         settings.generatedRootPath,
                         AssetListDir(settings),
                         PromptSetDir(settings),
                         SpriteSheetDir(settings),
                         CandidatesRoot(settings),
                         ConfirmedRoot(settings)
                     })
            {
                if (EnsureAssetFolder(folder))
                {
                    created.Add(folder);
                }
            }

            if (created.Count > 0)
            {
                Debug.Log($"[MCPTools] 작업 폴더가 없어 새로 만들었습니다: {string.Join(", ", created)}");
            }
        }

        /// <summary>
        /// Assets 아래 폴더를 (중간 폴더까지 재귀적으로) 보장합니다.
        /// AssetDatabase 기준으로 만들기 때문에 별도의 Refresh 없이 프로젝트 창에 바로 나타납니다.
        /// </summary>
        /// <param name="folderPath">Assets 기준 폴더 경로 (예: "Assets/Docs").</param>
        /// <returns>이 호출로 대상 폴더를 새로 만들었으면 true, 이미 있었거나 만들 수 없는 경로면 false.</returns>
        internal static bool EnsureAssetFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
            {
                return false;
            }

            string normalized = folderPath.Replace('\\', '/').TrimEnd('/');

            // AssetDatabase로 만들 수 있는 것은 프로젝트의 Assets 아래뿐이다
            // (읽기 전용 패키지 경로나 프로젝트 밖 경로는 대상이 아니다).
            if (normalized != "Assets" && !normalized.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return false;
            }

            if (AssetDatabase.IsValidFolder(normalized))
            {
                return false;
            }

            int slash = normalized.LastIndexOf('/');
            if (slash <= 0)
            {
                return false; // "Assets" 등 더 거슬러 올라갈 상위 폴더가 없음
            }

            EnsureAssetFolder(normalized.Substring(0, slash));
            string guid = AssetDatabase.CreateFolder(normalized.Substring(0, slash), normalized.Substring(slash + 1));
            return !string.IsNullOrEmpty(guid);
        }
    }
}
