// 2D Sprite 패키지(com.unity.2d.sprite) 의존 코드는 이 어셈블리(AIAssetPipeline.Editor.SpriteSlicing)에 격리한다.
// asmdef의 defineConstraints(AIASSETPIPELINE_HAS_2D_SPRITE) 덕분에 패키지가 없는 프로젝트에서는
// 어셈블리 자체가 컴파일 대상에서 제외되어, 본체(AIAssetPipeline.Editor)는 정상 컴파일된다.
// 의존 방향은 SpriteSlicing → AIAssetPipeline.Editor 단방향이며, 본체는 훅(SpriteSheetImporter.SpriteRectWriter)으로만 호출한다.
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

namespace AIAssetPipeline.Editor.SpriteSlicing
{
    /// <summary>
    /// <see cref="SpriteSheetImporter.SpriteRectWriter"/> 훅에 Sprite Editor 데이터 프로바이더 기반
    /// 슬라이스 기록 구현을 등록합니다. 2D Sprite 패키지가 설치된 프로젝트에서만 컴파일·등록됩니다.
    /// </summary>
    [InitializeOnLoad]
    internal static class SpriteSliceWriter
    {
        static SpriteSliceWriter()
        {
            SpriteSheetImporter.SpriteRectWriter = WriteSpriteRects;
        }

        /// <summary>
        /// 텍스처 임포터의 슬라이스 목록을 <see cref="ISpriteEditorDataProvider"/>로 덮어씁니다.
        /// (이전 슬라이스 데이터를 완전히 대체해 stale 잔재를 제거)
        /// 호출자가 이어서 <c>SaveAndReimport()</c>를 수행합니다.
        /// </summary>
        private static void WriteSpriteRects(TextureImporter importer, List<SpriteMetaData> metas)
        {
            var factories = new SpriteDataProviderFactories();
            factories.Init();
            ISpriteEditorDataProvider provider = factories.GetSpriteEditorDataProviderFromObject(importer);
            provider.InitSpriteEditorDataProvider();

            var rects = new SpriteRect[metas.Count];
            for (int i = 0; i < metas.Count; i++)
            {
                SpriteMetaData m = metas[i];
                rects[i] = new SpriteRect
                {
                    name = m.name,
                    spriteID = GUID.Generate(),
                    rect = m.rect,
                    alignment = (SpriteAlignment)m.alignment,
                    pivot = m.pivot,
                    border = Vector4.zero
                };
            }

            provider.SetSpriteRects(rects);

            // 이름↔fileID 매핑 테이블도 갱신해 슬라이스 이름이 서브에셋에 반영되게 한다.
            var nameIdProvider = provider.GetDataProvider<ISpriteNameFileIdDataProvider>();
            if (nameIdProvider != null)
            {
                var pairs = new List<SpriteNameFileIdPair>(rects.Length);
                foreach (SpriteRect r in rects)
                {
                    pairs.Add(new SpriteNameFileIdPair(r.name, r.spriteID));
                }

                nameIdProvider.SetNameFileIdPairs(pairs);
            }

            provider.Apply();
        }
    }
}
