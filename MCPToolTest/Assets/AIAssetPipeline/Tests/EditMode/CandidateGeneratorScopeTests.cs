using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace AIAssetPipeline.Editor.Tests
{
    /// <summary>
    /// <see cref="CandidateGenerator"/>의 PromptSet 단위 저장 스코프 규칙을 고정하는 테스트입니다.
    /// <para>
    /// 스코프 키(PromptSet 파일명 기반)와 후보 폴더의 읽기 폴백 순서
    /// (스코프 하위 → 스코프 없는 위치 → 구 위치)는 기존 프로젝트의 후보/확정본을
    /// 계속 읽기 위한 호환 계약이므로, 여기 적힌 결과는 유지돼야 합니다.
    /// </para>
    /// <para>
    /// <see cref="CandidateGenerator.SanitizeScope"/> 등 internal 멤버는
    /// <c>Editor/AssemblyInfo.cs</c>의 <c>InternalsVisibleTo</c>로 이 어셈블리에 공개되어 있습니다.
    /// </para>
    /// </summary>
    public class CandidateGeneratorScopeTests
    {
        private AIAssetPipelineSettings _settings;
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            // 에셋으로 저장하지 않는 메모리 전용 인스턴스 — 프로젝트의 설정 에셋을 건드리지 않는다.
            _settings = ScriptableObject.CreateInstance<AIAssetPipelineSettings>();

            // 프로젝트 밖(시스템 임시 폴더)을 생성 루트로 써서 실제 Assets 폴더를 오염시키지 않는다.
            _tempRoot = Path.Combine(Path.GetTempPath(), "AIAssetPipelineScopeTests_" + Guid.NewGuid().ToString("N"))
                .Replace('\\', '/');
            Directory.CreateDirectory(_tempRoot);
            _settings.generatedRootPath = _tempRoot;
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings != null)
            {
                UnityEngine.Object.DestroyImmediate(_settings);
                _settings = null;
            }

            if (!string.IsNullOrEmpty(_tempRoot) && Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }

            _tempRoot = null;
        }

        // ──────────────────── 스코프 키 규칙 ────────────────────

        /// <summary>스코프 키는 PromptSet 파일명에서 확장자를 뗀 값임을 고정합니다.</summary>
        [Test]
        public void ScopeFromPromptSetPath_UsesFileNameWithoutExtension()
        {
            Assert.AreEqual("PromptSet_20260721_0512",
                CandidateGenerator.ScopeFromPromptSetPath("Assets/Docs/2_PromptSet/PromptSet_20260721_0512.json"));
            Assert.AreEqual("PromptSet_x",
                CandidateGenerator.ScopeFromPromptSetPath(@"Assets\Docs\PromptSet_x.json"), "역슬래시 경로도 허용");
            Assert.AreEqual(string.Empty, CandidateGenerator.ScopeFromPromptSetPath(null));
            Assert.AreEqual(string.Empty, CandidateGenerator.ScopeFromPromptSetPath(string.Empty));
        }

        /// <summary>폴더 이름으로 쓸 수 없는 문자가 '_'로 치환되는지 고정합니다.</summary>
        [Test]
        public void SanitizeScope_ReplacesInvalidFileNameChars()
        {
            Assert.AreEqual("a_b_c", CandidateGenerator.SanitizeScope("a/b:c"));
            Assert.AreEqual(string.Empty, CandidateGenerator.SanitizeScope(null));
            Assert.AreEqual(string.Empty, CandidateGenerator.SanitizeScope(string.Empty));
        }

        /// <summary>"." 만으로 이루어진 스코프(상위 폴더 탈출 위험)는 빈 스코프로 취급하는지 고정합니다.</summary>
        [Test]
        public void SanitizeScope_RejectsDotOnlyScopes()
        {
            Assert.AreEqual(string.Empty, CandidateGenerator.SanitizeScope("."));
            Assert.AreEqual(string.Empty, CandidateGenerator.SanitizeScope(".."));
            Assert.AreEqual(string.Empty, CandidateGenerator.SanitizeScope("..."));
        }

        // ──────────────────── 후보 폴더 읽기 폴백 ────────────────────

        /// <summary>
        /// 스코프 지정 시 읽기 폴백 순서를 고정합니다:
        /// 스코프 하위 폴더 → 스코프 없는 위치(3_Candidates/{id}) → 구 위치(Candidates/{id}) → 스코프 하위(새 대상).
        /// </summary>
        [Test]
        public void GetCandidateFolder_WithScope_FallsBackInOrder()
        {
            const string scope = "PromptSet_A";
            const string id = "item_001";
            string scoped = $"{_tempRoot}/3_Candidates/{scope}/{id}";
            string unscoped = $"{_tempRoot}/3_Candidates/{id}";
            string legacy = $"{_tempRoot}/Candidates/{id}";

            // ① 아무 폴더도 없음 → 스코프 하위(새로 만들 대상 경로)
            Assert.AreEqual(scoped, CandidateGenerator.GetCandidateFolder(_settings, id, scope));

            // ② 구 위치에만 있음 → 구 위치
            Directory.CreateDirectory(legacy);
            Assert.AreEqual(legacy, CandidateGenerator.GetCandidateFolder(_settings, id, scope));

            // ③ 스코프 없는 위치가 생기면 그쪽이 이긴다
            Directory.CreateDirectory(unscoped);
            Assert.AreEqual(unscoped, CandidateGenerator.GetCandidateFolder(_settings, id, scope));

            // ④ 스코프 하위 폴더가 생기면 항상 스코프 하위가 이긴다
            Directory.CreateDirectory(scoped);
            Assert.AreEqual(scoped, CandidateGenerator.GetCandidateFolder(_settings, id, scope));
        }

        /// <summary>스코프 미지정 시 기존 동작(새 위치 → 구 위치 폴백)이 유지되는지 고정합니다.</summary>
        [Test]
        public void GetCandidateFolder_WithoutScope_KeepsLegacyBehavior()
        {
            const string id = "item_001";
            string current = $"{_tempRoot}/3_Candidates/{id}";
            string legacy = $"{_tempRoot}/Candidates/{id}";

            Assert.AreEqual(current, CandidateGenerator.GetCandidateFolder(_settings, id));

            Directory.CreateDirectory(legacy);
            Assert.AreEqual(legacy, CandidateGenerator.GetCandidateFolder(_settings, id));

            Directory.CreateDirectory(current);
            Assert.AreEqual(current, CandidateGenerator.GetCandidateFolder(_settings, id));
        }

        /// <summary>
        /// 쓰기 폴더는 폴백을 따라가지 않고 스코프 하위를 고집하는지 고정합니다 —
        /// 폴백을 따라가면 생성 시 ClearFolder가 다른 PromptSet의 후보를 지웁니다.
        /// </summary>
        [Test]
        public void GetCandidateWriteFolder_WithScope_IgnoresLegacyFolders()
        {
            const string scope = "PromptSet_A";
            const string id = "item_001";
            Directory.CreateDirectory($"{_tempRoot}/3_Candidates/{id}");
            Directory.CreateDirectory($"{_tempRoot}/Candidates/{id}");

            Assert.AreEqual($"{_tempRoot}/3_Candidates/{scope}/{id}",
                CandidateGenerator.GetCandidateWriteFolder(_settings, id, scope));

            // 스코프가 없으면 읽기 폴백과 같은 위치(기존 동작)를 쓴다.
            Assert.AreEqual($"{_tempRoot}/3_Candidates/{id}",
                CandidateGenerator.GetCandidateWriteFolder(_settings, id, null));
        }
    }
}
