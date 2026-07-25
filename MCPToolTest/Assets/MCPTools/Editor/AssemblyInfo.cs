using System.Runtime.CompilerServices;

// MCPTools.Editor 어셈블리의 internal 멤버를 EditMode 테스트 어셈블리에 공개합니다.
//
// 이유 (Task 9 Q2):
//   테스트 대상 중 MCPToolFolders(Editor/Common/MCPToolFolders.cs)와
//   SpriteSheetDetection.Pixels가 internal이라, 이 선언 없이는
//   Tests/EditMode 에서 폴더 경로 조합·읽기 폴백·배경 제거 결과를 검증할 수 없습니다.
//   테스트를 위해 public API를 늘리지 않기 위한 조치이며, 배포 패키지의 공개 표면은 그대로 유지됩니다.
//
// 주의:
//   - 대상 어셈블리 이름은 Tests/EditMode/MCPTools.Editor.Tests.asmdef 의 "name" 값과 반드시 일치해야 합니다.
//   - 테스트 어셈블리는 defineConstraints("UNITY_INCLUDE_TESTS")로 묶여 있어
//     UPM으로 설치한 사용자 프로젝트에서는 컴파일되지 않습니다. 이 선언은 그때도 무해합니다
//     (존재하지 않는 어셈블리를 가리키는 InternalsVisibleTo는 오류가 아닙니다).
[assembly: InternalsVisibleTo("MCPTools.Editor.Tests")]
