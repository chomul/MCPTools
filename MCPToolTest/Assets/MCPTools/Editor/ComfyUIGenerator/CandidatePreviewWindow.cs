using System.IO;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// 이미지 후보를 원본 해상도로 크게 보여주는 유틸리티 창입니다.
    /// 파일 바이트를 직접 로드해 임포터 축소 설정과 무관하게 원본 해상도를 표시합니다.
    /// </summary>
    public class CandidatePreviewWindow : EditorWindow
    {
        private Texture2D _texture;
        private string _assetPath = string.Empty;
        private bool _ownsTexture;

        /// <summary>후보 이미지 미리 보기 창을 엽니다.</summary>
        /// <param name="assetPath">Assets/ 기준 상대 경로.</param>
        /// <param name="seed">창 제목에 표시할 시드.</param>
        public static void Open(string assetPath, long seed)
        {
            var window = GetWindow<CandidatePreviewWindow>(utility: true,
                title: $"후보 미리 보기 — 시드 {seed}", focus: true);
            window.minSize = new Vector2(240f, 240f);
            window.position = new Rect(window.position.x, window.position.y, 720f, 720f);
            window.LoadTexture(assetPath);
            window.Show();
        }

        /// <summary>가능하면 파일 바이트로 원본 해상도 텍스처를 로드하고, 실패하면 AssetDatabase 로드본을 사용합니다.</summary>
        private void LoadTexture(string assetPath)
        {
            ReleaseTexture();
            _assetPath = assetPath ?? string.Empty;

            if (File.Exists(_assetPath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(_assetPath);
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    if (texture.LoadImage(bytes))
                    {
                        _texture = texture;
                        _ownsTexture = true;
                        return;
                    }

                    DestroyImmediate(texture);
                }
                catch (System.Exception)
                {
                    // 파일 로드 실패 시 아래의 AssetDatabase 로드로 대체한다.
                }
            }

            _texture = AssetDatabase.LoadAssetAtPath<Texture2D>(_assetPath);
            _ownsTexture = false;
        }

        private void OnDisable()
        {
            ReleaseTexture();
        }

        private void ReleaseTexture()
        {
            if (_ownsTexture && _texture != null)
            {
                DestroyImmediate(_texture);
            }

            _texture = null;
            _ownsTexture = false;
        }

        private void OnGUI()
        {
            if (_texture == null)
            {
                EditorGUILayout.HelpBox($"이미지를 로드하지 못했습니다: {_assetPath}", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField(
                $"{Path.GetFileName(_assetPath)}  ·  {_texture.width} x {_texture.height}",
                EditorStyles.miniLabel);

            Rect rect = GUILayoutUtility.GetRect(position.width,
                position.height - EditorGUIUtility.singleLineHeight - 8f);
            GUI.DrawTexture(rect, _texture, ScaleMode.ScaleToFit);
        }
    }
}
