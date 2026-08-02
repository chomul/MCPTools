namespace AIAssetPipeline.Runtime
{
    /// <summary>
    /// AI Asset Pipeline 패키지의 전역 정보(버전 등)를 제공하는 정적 클래스입니다.
    /// </summary>
    public static class AIAssetPipelineInfo
    {
        /// <summary>패키지 버전 문자열입니다.</summary>
        public const string Version = "0.7.0";
    }

    /// <summary>
    /// 생성 대상 에셋의 종류입니다. 파이프라인 각 단계의 데이터 모델에서 공용으로 사용합니다.
    /// </summary>
    public enum AssetKind
    {
        /// <summary>일반 이미지 (스프라이트/텍스처).</summary>
        Image,

        /// <summary>uGUI UI 요소용 이미지.</summary>
        UI,

        /// <summary>사운드 (AudioClip).</summary>
        Audio
    }
}
