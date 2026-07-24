using System;
using System.Collections.Generic;
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
        /// <summary>
        /// 설정의 문서 루트(<c>docsRootPath</c>)와 생성 결과 루트(<c>generatedRootPath</c>) 폴더를 보장합니다.
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
            if (EnsureAssetFolder(settings.docsRootPath))
            {
                created.Add(settings.docsRootPath);
            }

            if (EnsureAssetFolder(settings.generatedRootPath))
            {
                created.Add(settings.generatedRootPath);
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
