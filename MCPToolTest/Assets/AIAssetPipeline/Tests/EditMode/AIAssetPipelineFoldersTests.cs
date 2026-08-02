using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AIAssetPipeline.Editor.Tests
{
    /// <summary>
    /// <see cref="AIAssetPipelineFolders"/>의 폴더 경로 조합·신구 위치 폴백·경로 검증을 고정하는 테스트입니다.
    /// <para>
    /// 단계별 하위 폴더 이름(<c>1_AssetList</c>, <c>3_Confirmed</c> 등)과 폴백 규칙은 기존 프로젝트의
    /// 산출물을 계속 읽기 위한 호환 계약입니다. Task 8 C27(정렬 개선)이 이 부분을 손대더라도
    /// 여기 적힌 결과는 유지돼야 합니다.
    /// </para>
    /// <para>
    /// <see cref="AIAssetPipelineFolders"/>는 internal이므로 <c>Editor/AssemblyInfo.cs</c>의
    /// <c>InternalsVisibleTo</c>로 이 테스트 어셈블리에 공개되어 있습니다.
    /// </para>
    /// </summary>
    public class AIAssetPipelineFoldersTests
    {
        private AIAssetPipelineSettings _settings;
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            // 에셋으로 저장하지 않는 메모리 전용 인스턴스 — 프로젝트의 설정 에셋을 건드리지 않는다.
            _settings = ScriptableObject.CreateInstance<AIAssetPipelineSettings>();
            _settings.docsRootPath = "Assets/MyDocs";
            _settings.generatedRootPath = "Assets/MyGen";
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

        /// <summary>프로젝트 밖(시스템 임시 폴더)에 테스트용 작업 폴더를 만듭니다.</summary>
        private string CreateTempRoot()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "AIAssetPipelineFolderTests_" + Guid.NewGuid().ToString("N"))
                .Replace('\\', '/');
            Directory.CreateDirectory(_tempRoot);
            return _tempRoot;
        }

        // ──────────────────── 폴더 이름 상수 ────────────────────

        /// <summary>단계별 하위 폴더 이름을 고정합니다. (디스크에 남은 기존 산출물과 맞물리는 값)</summary>
        [Test]
        public void SubfolderNames_AreStable()
        {
            Assert.AreEqual("1_AssetList", AIAssetPipelineFolders.AssetListFolder);
            Assert.AreEqual("2_PromptSet", AIAssetPipelineFolders.PromptSetFolder);
            Assert.AreEqual("SpriteSheetPrompt", AIAssetPipelineFolders.SpriteSheetFolder);
            Assert.AreEqual("3_Candidates", AIAssetPipelineFolders.CandidatesFolder);
            Assert.AreEqual("3_Confirmed", AIAssetPipelineFolders.ConfirmedFolder);
            Assert.AreEqual("SpriteSheets", AIAssetPipelineFolders.SpriteSheetsFolder);
            Assert.AreEqual("Animations", AIAssetPipelineFolders.AnimationsFolder);
            Assert.AreEqual("Candidates", AIAssetPipelineFolders.LegacyCandidatesFolder);
        }

        // ──────────────────── 경로 조합 ────────────────────

        /// <summary>설정 루트에서 단계별 폴더 경로가 조합되는 규칙을 고정합니다.</summary>
        [Test]
        public void DirectoryComposition_FollowsRootPlusSubfolder()
        {
            Assert.AreEqual("Assets/MyDocs", AIAssetPipelineFolders.DocsRoot(_settings));
            Assert.AreEqual("Assets/MyGen", AIAssetPipelineFolders.GeneratedRoot(_settings));

            Assert.AreEqual("Assets/MyDocs/1_AssetList", AIAssetPipelineFolders.AssetListDir(_settings));
            Assert.AreEqual("Assets/MyDocs/2_PromptSet", AIAssetPipelineFolders.PromptSetDir(_settings));
            Assert.AreEqual("Assets/MyDocs/SpriteSheetPrompt", AIAssetPipelineFolders.SpriteSheetDir(_settings));

            Assert.AreEqual("Assets/MyGen/3_Candidates", AIAssetPipelineFolders.CandidatesRoot(_settings));
            Assert.AreEqual("Assets/MyGen/3_Confirmed", AIAssetPipelineFolders.ConfirmedRoot(_settings));

            // 시트/애니메이션은 확정본 아래로 한 단계 더 들어간다.
            Assert.AreEqual("Assets/MyGen/3_Confirmed/SpriteSheets", AIAssetPipelineFolders.SpriteSheetsDir(_settings));
            Assert.AreEqual("Assets/MyGen/3_Confirmed/Animations", AIAssetPipelineFolders.AnimationsDir(_settings));
        }

        /// <summary>루트 경로의 역슬래시와 끝 슬래시가 정규화되는지 고정합니다.</summary>
        [Test]
        public void Roots_NormalizeSeparatorsAndTrailingSlash()
        {
            _settings.docsRootPath = @"Assets\My Docs\";
            _settings.generatedRootPath = "Assets/My Gen///";

            Assert.AreEqual("Assets/My Docs", AIAssetPipelineFolders.DocsRoot(_settings));
            Assert.AreEqual("Assets/My Gen", AIAssetPipelineFolders.GeneratedRoot(_settings));
            Assert.AreEqual("Assets/My Docs/1_AssetList", AIAssetPipelineFolders.AssetListDir(_settings));
        }

        /// <summary>설정이 null이거나 값이 비면 표준 기본 경로를 쓰는지 고정합니다.</summary>
        [Test]
        public void Roots_FallBackToDefaults_WhenSettingsOrValuesAreEmpty()
        {
            Assert.AreEqual("Assets/Docs", AIAssetPipelineFolders.DocsRoot(null));
            Assert.AreEqual("Assets/Generated", AIAssetPipelineFolders.GeneratedRoot(null));

            _settings.docsRootPath = string.Empty;
            _settings.generatedRootPath = null;

            Assert.AreEqual("Assets/Docs", AIAssetPipelineFolders.DocsRoot(_settings));
            Assert.AreEqual("Assets/Generated", AIAssetPipelineFolders.GeneratedRoot(_settings));
            Assert.AreEqual("Assets/Generated/3_Confirmed/SpriteSheets", AIAssetPipelineFolders.SpriteSheetsDir(_settings));
        }

        // ──────────────────── ResolveForRead (신·구 위치 폴백) ────────────────────

        /// <summary>새 위치 파일이 있으면 새 위치를, 없고 구 위치에 있으면 구 위치를 고르는지 고정합니다.</summary>
        [Test]
        public void ResolveForRead_PrefersNewLocation_FallsBackToLegacy()
        {
            string root = CreateTempRoot();
            string preferred = $"{root}/1_AssetList/AssetList_x.json";
            string legacy = $"{root}/AssetList_x.json";
            Directory.CreateDirectory($"{root}/1_AssetList");

            // ① 둘 다 없음 → 새 위치(= 새로 만들 대상 경로)
            Assert.AreEqual(preferred, AIAssetPipelineFolders.ResolveForRead(preferred, legacy));

            // ② 구 위치에만 있음 → 구 위치
            File.WriteAllText(legacy, "{}");
            Assert.AreEqual(legacy, AIAssetPipelineFolders.ResolveForRead(preferred, legacy));

            // ③ 둘 다 있음 → 새 위치가 이긴다
            File.WriteAllText(preferred, "{}");
            Assert.AreEqual(preferred, AIAssetPipelineFolders.ResolveForRead(preferred, legacy));
        }

        /// <summary>빈 경로가 섞여도 예외 없이 처리되는지 고정합니다.</summary>
        [Test]
        public void ResolveForRead_HandlesNullAndEmptyPaths()
        {
            string root = CreateTempRoot();
            string legacy = $"{root}/AssetList_x.json";
            File.WriteAllText(legacy, "{}");

            Assert.AreEqual(legacy, AIAssetPipelineFolders.ResolveForRead(null, legacy));
            Assert.AreEqual(legacy, AIAssetPipelineFolders.ResolveForRead(string.Empty, legacy));
            Assert.IsNull(AIAssetPipelineFolders.ResolveForRead(null, null));
            Assert.AreEqual($"{root}/missing.json", AIAssetPipelineFolders.ResolveForRead($"{root}/missing.json", null));
        }

        // ──────────────────── FindDocuments (신·구 위치 병합) ────────────────────

        /// <summary>
        /// 새 하위 폴더와 구 위치(루트)를 함께 훑고, 파일명이 겹치면 새 위치를 채택하며,
        /// 결과가 최신 수정 순으로 정렬되는지 고정합니다.
        /// </summary>
        [Test]
        public void FindDocuments_MergesLegacyAndNewLocation_NewWins_SortedByWriteTimeDesc()
        {
            string root = CreateTempRoot();
            string sub = $"{root}/{AIAssetPipelineFolders.AssetListFolder}";
            Directory.CreateDirectory(sub);

            string legacyOnly = $"{root}/AssetList_legacy.json";
            string legacyDup = $"{root}/AssetList_dup.json";
            string newOnly = $"{sub}/AssetList_new.json";
            string newDup = $"{sub}/AssetList_dup.json";
            string unrelated = $"{root}/Other.txt";

            foreach (string path in new[] { legacyOnly, legacyDup, newOnly, newDup, unrelated })
            {
                File.WriteAllText(path, "{}");
            }

            // 구 위치의 중복 파일을 일부러 "가장 최신"으로 만들어, 시간이 아니라 위치로 이긴다는 점을 확인한다.
            File.SetLastWriteTime(legacyDup, new DateTime(2026, 1, 5));
            File.SetLastWriteTime(newOnly, new DateTime(2026, 1, 3));
            File.SetLastWriteTime(newDup, new DateTime(2026, 1, 2));
            File.SetLastWriteTime(legacyOnly, new DateTime(2026, 1, 1));

            string[] found = AIAssetPipelineFolders.FindDocuments(root, AIAssetPipelineFolders.AssetListFolder, "AssetList_*.json");

            Assert.AreEqual(3, found.Length, "패턴이 다른 파일(Other.txt)은 제외되고 중복은 1개로 합쳐집니다.");
            StringAssert.EndsWith($"{AIAssetPipelineFolders.AssetListFolder}/AssetList_new.json", found[0]);
            StringAssert.EndsWith($"{AIAssetPipelineFolders.AssetListFolder}/AssetList_dup.json", found[1]);
            StringAssert.EndsWith("/AssetList_legacy.json", found[2]);
            Assert.IsFalse(found[2].Contains(AIAssetPipelineFolders.AssetListFolder), "구 위치 파일은 루트 경로 그대로여야 합니다.");
        }

        /// <summary>폴더가 아예 없으면 예외 없이 빈 배열을 돌려주는지 고정합니다.</summary>
        [Test]
        public void FindDocuments_MissingFolders_ReturnsEmpty()
        {
            string missing = Path.Combine(Path.GetTempPath(), "AIAssetPipelineMissing_" + Guid.NewGuid().ToString("N"))
                .Replace('\\', '/');

            string[] found = AIAssetPipelineFolders.FindDocuments(
                missing, AIAssetPipelineFolders.AssetListFolder, "AssetList_*.json");

            Assert.IsNotNull(found);
            Assert.AreEqual(0, found.Length);
        }

        // ──────────────────── EnsureAssetFolder (경로 검증) ────────────────────

        /// <summary>
        /// AssetDatabase로 만들 수 없는 경로(빈 값, Assets 밖, 대소문자 불일치)는 false를 돌려주고
        /// 아무것도 만들지 않는지 고정합니다.
        /// </summary>
        [Test]
        public void EnsureAssetFolder_RejectsPathsOutsideAssets_WithoutCreatingAnything()
        {
            Assert.IsFalse(AIAssetPipelineFolders.EnsureAssetFolder(null), "null");
            Assert.IsFalse(AIAssetPipelineFolders.EnsureAssetFolder(string.Empty), "빈 문자열");
            Assert.IsFalse(AIAssetPipelineFolders.EnsureAssetFolder("assets/lowercase"), "Ordinal 비교라 대소문자가 달라도 거부");
            Assert.IsFalse(AIAssetPipelineFolders.EnsureAssetFolder("AssetsLike/Folder"), "\"Assets/\" 접두사가 아님");
            Assert.IsFalse(AIAssetPipelineFolders.EnsureAssetFolder("../Outside"), "프로젝트 밖 상대 경로");
            Assert.IsFalse(AIAssetPipelineFolders.EnsureAssetFolder("C:/Temp/Folder"), "절대 경로");

            const string packagePath = "Packages/com.sungchan.aiassetpipeline/Tests_TempFolder";
            Assert.IsFalse(AIAssetPipelineFolders.EnsureAssetFolder(packagePath), "읽기 전용 패키지 경로");
            Assert.IsFalse(AssetDatabase.IsValidFolder(packagePath), "거부된 경로에 폴더가 만들어지면 안 됩니다.");
        }

        /// <summary>
        /// 이미 있는 폴더에는 false(=이번 호출로 만들지 않음)를 돌려주고,
        /// 역슬래시·끝 슬래시가 정규화되는지 고정합니다.
        /// </summary>
        [Test]
        public void EnsureAssetFolder_ExistingFolder_ReturnsFalse_AndNormalizesSeparators()
        {
            Assert.IsTrue(AssetDatabase.IsValidFolder("Assets"), "사전 조건: Assets 폴더");

            Assert.IsFalse(AIAssetPipelineFolders.EnsureAssetFolder("Assets"));
            Assert.IsFalse(AIAssetPipelineFolders.EnsureAssetFolder("Assets/"), "끝 슬래시 정규화");
            Assert.IsFalse(AIAssetPipelineFolders.EnsureAssetFolder(@"Assets\"), "역슬래시 정규화");
        }
    }
}
