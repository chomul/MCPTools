<#
.SYNOPSIS
    Assets/MCPTools/ 폴더를 배포용 zip으로 패키징합니다.

.DESCRIPTION
    .unitypackage를 쓰지 않는 이유: 브리지 서버가 들어 있는
    Editor/ComfyUIGenerator/Server~/ 폴더는 이름 끝의 '~' 때문에 Unity가
    에셋으로 임포트하지 않습니다. Export Package는 임포트된 에셋만 내보내므로
    이 폴더가 경고 없이 누락되고, 받은 쪽에서 3단계(생성)가 동작하지 않습니다.
    폴더를 파일시스템 그대로 복사하는 zip 방식만 사용합니다.

    받는 사람은 압축을 풀어 나온 MCPTools 폴더를 자기 프로젝트의 Assets/ 아래에
    그대로 넣으면 됩니다.

.EXAMPLE
    .\tools\pack-mcptools.ps1
    .\tools\pack-mcptools.ps1 -OutputDir D:\배포
#>
[CmdletBinding()]
param(
    # zip을 만들 폴더. 기본값은 저장소 루트의 dist/
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$sourceDir = Join-Path $repoRoot 'MCPToolTest\Assets\MCPTools'

if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'dist' }

if (-not (Test-Path $sourceDir)) {
    throw "MCPTools 폴더를 찾을 수 없습니다: $sourceDir"
}

# 배포에서 제외할 항목 (와일드카드는 전체 경로에 대해 -like 로 평가)
# 폴더 자체도 지워야 하므로 '*\__pycache__' (하위 없음) 패턴을 함께 둡니다.
$excludePatterns = @(
    '*\__pycache__'     # 파이썬 바이트코드 캐시 폴더
    '*\__pycache__\*'   # 그 안의 내용
    '*.pyc'
    '*\.DS_Store'
    '*\Thumbs.db'
)

# 스테이징 폴더에 복사 -> 제외 항목 삭제 -> 압축.
# (Compress-Archive 에는 제외 옵션이 없어서 스테이징을 거칩니다.)
$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("mcptools_pack_" + [guid]::NewGuid().ToString('N'))
$stagingDir  = Join-Path $stagingRoot 'MCPTools'

try {
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceDir '*') -Destination $stagingDir -Recurse -Force

    $removed = 0
    Get-ChildItem -Path $stagingDir -Recurse -Force | Sort-Object -Property FullName -Descending | ForEach-Object {
        $item = $_
        foreach ($pattern in $excludePatterns) {
            if ($item.FullName -like $pattern) {
                Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
                $removed++
                break
            }
        }
    }

    # 브리지 서버가 실제로 스테이징에 남아 있는지 확인 — 이게 누락되면 배포본이 무의미합니다.
    $bridge = Join-Path $stagingDir 'Editor\ComfyUIGenerator\Server~\bridge_server.py'
    if (-not (Test-Path $bridge)) {
        throw "브리지 서버가 누락되었습니다: Editor\ComfyUIGenerator\Server~\bridge_server.py"
    }

    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }

    # 먼저 임시 위치에 압축한 뒤 목적지로 옮긴다.
    # (기존 zip을 탐색기/압축 프로그램이 열어 두면 삭제가 막히는데,
    #  그때 압축 작업까지 통째로 실패하지 않도록 분리한다.)
    $tempZip = Join-Path $stagingRoot 'MCPTools.zip'
    Compress-Archive -Path $stagingDir -DestinationPath $tempZip -CompressionLevel Optimal

    $zipPath = Join-Path $OutputDir 'MCPTools.zip'
    $locked  = $false
    try {
        Move-Item -LiteralPath $tempZip -Destination $zipPath -Force -ErrorAction Stop
    }
    catch {
        # 목적지가 잠겨 있으면 옆에 다른 이름으로 남기고 안내한다.
        $locked  = $true
        $zipPath = Join-Path $OutputDir 'MCPTools.new.zip'
        if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
        Move-Item -LiteralPath $tempZip -Destination $zipPath -Force
    }

    $fileCount = (Get-ChildItem -Path $stagingDir -Recurse -File -Force).Count
    $sizeKb    = [math]::Round((Get-Item $zipPath).Length / 1KB, 1)

    Write-Host ""
    Write-Host "패키징 완료: $zipPath" -ForegroundColor Green
    Write-Host "  파일 $fileCount 개 / $sizeKb KB / 제외 $removed 건"
    Write-Host "  설치: 압축 해제 후 MCPTools 폴더를 대상 프로젝트의 Assets/ 아래에 복사"
    if ($locked) {
        Write-Host ""
        Write-Host "주의: 기존 MCPTools.zip이 다른 프로그램에 열려 있어 덮어쓰지 못했습니다." -ForegroundColor Yellow
        Write-Host "      새 결과물은 MCPTools.new.zip 으로 저장했습니다." -ForegroundColor Yellow
        Write-Host "      해당 프로그램(탐색기 미리보기, 압축 프로그램 등)을 닫고 다시 실행하세요." -ForegroundColor Yellow
    }
    Write-Host ""
}
finally {
    if (Test-Path $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
