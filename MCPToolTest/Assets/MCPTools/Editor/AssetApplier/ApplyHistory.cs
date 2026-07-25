using System;
using System.Collections.Generic;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// 4단계 적용 이력의 저장소입니다 (Task 10 R3).
    /// 이력은 3단계 확정 기록과 같은 문서(<c>{확정본 폴더}/GenerationResults.json</c>)의
    /// <c>applications</c> 배열에 쌓입니다. <c>results</c>(3단계 확정 기록)와 <b>형제 키</b>이며
    /// 양쪽 모두 문서 전체를 읽어 자기 배열만 갈아끼운 뒤 다시 쓰므로 서로 덮어쓰지 않습니다
    /// (<see cref="CandidateGenerator.LoadResultsDocument"/> / <see cref="CandidateGenerator.SaveResultsDocument"/>).
    ///
    /// 이력을 남기는 목적은 두 가지입니다.
    /// <list type="bullet">
    /// <item>창을 닫았다 다시 열어도 "적용됨" 배지가 유지된다.</item>
    /// <item>[일괄 적용]이 이미 적용한 항목을 다시 저장하지 않는다 (불필요한 프리팹 재저장·git diff 방지).</item>
    /// </list>
    ///
    /// 기록과 실제 프리팹/씬의 현재 값이 어긋나도(사용자가 손으로 되돌린 경우) <b>기록만 신뢰</b>합니다.
    /// 현재 값을 읽어 비교하지 않습니다 — 항목 수만큼 프리팹을 로드하는 비용을 창 리페인트마다 치르지 않기 위함입니다.
    /// 다시 적용해야 한다면 창에서 항목을 선택해 [선택 적용]을 누르면 됩니다(이미 적용된 항목도 항상 가능).
    /// </summary>
    /// <remarks>
    /// 이 클래스는 <c>AssetDatabase</c>를 호출하지 않습니다. 이력 파일은 File I/O로만 읽고 쓰며,
    /// 일괄 적용 중 항목마다 임포트를 유발하지 않게 하기 위한 의도적인 선택입니다
    /// (프로젝트 창 표시는 다음 리프레시 때 갱신됩니다).
    /// </remarks>
    internal static class ApplyHistory
    {
        /// <summary>확정 결과 문서에서 적용 이력 배열이 놓이는 키 이름입니다.</summary>
        internal const string ApplicationsKey = "applications";

        /// <summary>
        /// 성공한 적용 1건을 기록합니다. 같은 항목 ID의 기존 기록은 새 기록으로 대체됩니다.
        /// 실패한 결과(<c>success == false</c>)나 ID가 없는 항목은 기록하지 않습니다.
        /// </summary>
        /// <param name="item">적용한 항목.</param>
        /// <param name="result">적용 결과 (성공한 것만 기록됩니다).</param>
        /// <remarks>
        /// 이력 기록에 실패해도(문서 손상·쓰기 권한 없음 등) 적용 자체를 실패시키지 않기 위해
        /// 예외를 밖으로 던지지 않고 경고만 남깁니다.
        /// </remarks>
        internal static void Record(AssetListItem item, ApplyResult result)
        {
            if (item == null || string.IsNullOrEmpty(item.id) || result == null || !result.success)
            {
                return;
            }

            try
            {
                MCPToolSettings settings = MCPToolSettings.GetOrCreate();

                // 문서 전체를 읽어 applications만 갈아끼운다 (results·schemaVersion 등 나머지 키는 그대로 보존).
                Dictionary<string, object> doc = CandidateGenerator.LoadResultsDocument(settings);

                var applications = new List<object>();
                foreach (Dictionary<string, object> entry in ReadEntries(doc))
                {
                    if (GetString(entry, "assetItemId") != item.id)
                    {
                        applications.Add(entry); // 같은 항목의 기존 기록은 새 기록으로 대체
                    }
                }

                applications.Add(new Dictionary<string, object>
                {
                    { "assetItemId", item.id },
                    { "targetPrefabPath", Normalize(result.prefabPath) },
                    { "targetScenePath", Normalize(result.scenePath) },
                    { "targetObjectPath", Normalize(result.objectPath) },
                    { "appliedAssetPath", Normalize(result.appliedAssetPath) },
                    { "spriteName", Normalize(item.spriteName) },
                    { "linkedControllerPath", Normalize(result.linkedControllerPath) },
                    { "appliedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }
                });

                doc[ApplicationsKey] = applications;
                CandidateGenerator.SaveResultsDocument(settings, doc);
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"[MCPTools] 적용 이력을 기록하지 못했습니다 (항목 \"{item.id}\"): {e.Message} " +
                    "적용 자체는 완료되었지만, 창을 다시 열면 이 항목이 \"적용됨\"으로 표시되지 않을 수 있습니다.");
            }
        }

        /// <summary>
        /// 이력에 남아 있는 항목 ID 집합을 반환합니다 (창의 "적용됨" 배지 복원용).
        /// </summary>
        /// <param name="settings">설정 객체 (이력 파일 경로 해석에 사용).</param>
        /// <returns>
        /// 적용된 항목 ID 집합. 이력 문서가 없거나 읽을 수 없으면 빈 집합을 반환합니다
        /// (이력을 못 읽었다고 창이 열리지 않으면 안 되므로 예외를 던지지 않습니다).
        /// </returns>
        internal static HashSet<string> LoadAppliedItemIds(MCPToolSettings settings)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (settings == null)
            {
                return ids;
            }

            try
            {
                foreach (Dictionary<string, object> entry in
                         ReadEntries(CandidateGenerator.LoadResultsDocument(settings)))
                {
                    string id = GetString(entry, "assetItemId");
                    if (!string.IsNullOrEmpty(id))
                    {
                        ids.Add(id);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MCPTools] 적용 이력을 읽지 못해 빈 이력으로 진행합니다: {e.Message}");
            }

            return ids;
        }

        /// <summary>문서에서 적용 이력 항목들을 읽습니다 (키가 없거나 형태가 다르면 빈 목록).</summary>
        private static List<Dictionary<string, object>> ReadEntries(Dictionary<string, object> doc)
        {
            var entries = new List<Dictionary<string, object>>();

            object value;
            if (doc == null || !doc.TryGetValue(ApplicationsKey, out value))
            {
                return entries;
            }

            var list = value as List<object>;
            if (list == null)
            {
                return entries;
            }

            foreach (object entry in list)
            {
                var dict = entry as Dictionary<string, object>;
                if (dict != null)
                {
                    entries.Add(dict);
                }
            }

            return entries;
        }

        /// <summary>경로/이름 값을 기록용으로 정규화합니다 (null → 빈 문자열, 역슬래시 → 슬래시).</summary>
        private static string Normalize(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Replace('\\', '/');
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            object value;
            return dict != null && dict.TryGetValue(key, out value) && value is string s ? s : string.Empty;
        }
    }
}
