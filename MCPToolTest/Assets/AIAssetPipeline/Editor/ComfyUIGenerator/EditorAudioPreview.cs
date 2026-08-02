using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AIAssetPipeline.Editor
{
    /// <summary>
    /// 에디터에서 AudioClip을 미리 듣기 위한 정적 헬퍼입니다.
    /// UnityEditor 내부 AudioUtil을 리플렉션으로 호출하며, Unity 버전에 따라
    /// 달라질 수 있는 메서드명(PlayPreviewClip/PlayClip, StopAllPreviewClips/StopAllClips)을 순회 탐지합니다.
    /// </summary>
    public static class EditorAudioPreview
    {
        private static readonly string[] PlayMethodNames = { "PlayPreviewClip", "PlayClip" };
        private static readonly string[] StopMethodNames = { "StopAllPreviewClips", "StopAllClips" };

        private static Type _audioUtilType;
        private static bool _typeSearched;

        /// <summary>현재 재생 중인 클립의 에셋 경로입니다 (재생 중이 아니면 빈 문자열).</summary>
        public static string PlayingPath { get; private set; } = string.Empty;

        private static Type AudioUtilType
        {
            get
            {
                if (!_typeSearched)
                {
                    _typeSearched = true;
                    _audioUtilType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.AudioUtil");
                }

                return _audioUtilType;
            }
        }

        /// <summary>
        /// 클립을 재생합니다. 이미 다른 클립이 재생 중이면 먼저 정지합니다.
        /// </summary>
        /// <param name="clip">재생할 오디오 클립.</param>
        /// <param name="assetPath">재생 중 표시용 에셋 경로.</param>
        /// <param name="error">실패 시 사용자 안내 메시지 (성공 시 null).</param>
        /// <returns>재생 시작에 성공하면 true.</returns>
        public static bool Play(AudioClip clip, string assetPath, out string error)
        {
            error = null;
            if (clip == null)
            {
                error = "오디오 클립을 로드하지 못했습니다. 파일 임포트 상태를 확인해주세요.";
                return false;
            }

            Stop();

            Type type = AudioUtilType;
            if (type == null)
            {
                error = "UnityEditor.AudioUtil 타입을 찾을 수 없어 미리 듣기를 사용할 수 없습니다. (Unity 버전 API 변경)";
                return false;
            }

            foreach (string name in PlayMethodNames)
            {
                MethodInfo method = FindMethod(type, name, clip);
                if (method == null)
                {
                    continue;
                }

                try
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    var args = new object[parameters.Length];
                    args[0] = clip;
                    for (int i = 1; i < parameters.Length; i++)
                    {
                        args[i] = parameters[i].HasDefaultValue
                            ? parameters[i].DefaultValue
                            : GetDefault(parameters[i].ParameterType);
                    }

                    method.Invoke(null, args);
                    PlayingPath = assetPath ?? string.Empty;
                    return true;
                }
                catch (Exception e)
                {
                    error = $"오디오 미리 듣기 호출에 실패했습니다: {e.Message}";
                    return false;
                }
            }

            error = "AudioUtil 재생 메서드를 찾지 못해 미리 듣기를 사용할 수 없습니다. (Unity 버전 API 변경)";
            return false;
        }

        /// <summary>모든 미리 듣기 재생을 정지합니다. 실패해도 예외를 던지지 않습니다.</summary>
        public static void Stop()
        {
            PlayingPath = string.Empty;

            Type type = AudioUtilType;
            if (type == null)
            {
                return;
            }

            foreach (string name in StopMethodNames)
            {
                MethodInfo method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public,
                    null, Type.EmptyTypes, null);
                if (method == null)
                {
                    continue;
                }

                try
                {
                    method.Invoke(null, null);
                }
                catch (Exception)
                {
                    // 정지 실패는 무시한다 (사용자 흐름을 막지 않음).
                }

                return;
            }
        }

        /// <summary>해당 경로의 클립이 이 헬퍼로 재생 중인지 여부입니다.</summary>
        public static bool IsPlaying(string assetPath)
        {
            return !string.IsNullOrEmpty(PlayingPath) && PlayingPath == assetPath;
        }

        /// <summary>첫 파라미터가 AudioClip인 정적 메서드를 찾습니다.</summary>
        private static MethodInfo FindMethod(Type type, string name, AudioClip clip)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public))
            {
                if (method.Name != name)
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length >= 1 && parameters[0].ParameterType.IsAssignableFrom(clip.GetType()))
                {
                    return method;
                }
            }

            return null;
        }

        private static object GetDefault(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
