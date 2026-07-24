using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MCPTools.Editor
{
    /// <summary>PC에 설치된 것으로 감지된 AI CLI 도구 1개의 정보입니다.</summary>
    public struct AiCliTool
    {
        /// <summary>사용자에게 보여줄 표시명 (예: "Claude Code (claude)").</summary>
        public string displayName;

        /// <summary>실행 커맨드 이름 (예: "claude").</summary>
        public string command;

        /// <summary>PATH에서 확인된 실제 실행 파일 경로.</summary>
        public string resolvedPath;
    }

    /// <summary>AI CLI 실행 결과입니다.</summary>
    public class AiCliResult
    {
        /// <summary>성공 여부 (종료 코드 0 그리고 타임아웃/취소 아님).</summary>
        public bool success;

        /// <summary>프로세스 종료 코드.</summary>
        public int exitCode;

        /// <summary>표준 출력 전문 (AI 응답).</summary>
        public string stdout = string.Empty;

        /// <summary>표준 오류 출력 전문.</summary>
        public string stderr = string.Empty;

        /// <summary>실패 시 한국어 안내 메시지.</summary>
        public string errorMessage = string.Empty;

        /// <summary>타임아웃으로 중단되었는지 여부.</summary>
        public bool timedOut;

        /// <summary>사용자 취소로 중단되었는지 여부.</summary>
        public bool canceled;
    }

    /// <summary>
    /// PC에 설치된 AI CLI 도구(claude/codex/gemini/cursor-agent/copilot 등)를 PATH에서 감지하고,
    /// 프롬프트를 비대화형(헤드리스)으로 실행해 stdout을 캡처하는 공통 유틸리티입니다.
    /// 파이프라인 여러 단계에서 재사용합니다. 경로는 하드코딩하지 않고 PATH 탐지만 사용합니다.
    /// </summary>
    public static class AiCliRunner
    {
        // 알려진 AI CLI: 커맨드명 → 표시명
        private static readonly (string command, string displayName)[] KnownTools =
        {
            ("claude", "Claude Code (claude)"),
            ("codex", "OpenAI Codex CLI (codex)"),
            ("gemini", "Gemini CLI (gemini)"),
            ("cursor-agent", "Cursor CLI (cursor-agent)"),
            ("copilot", "GitHub Copilot CLI (copilot)")
        };

        private static List<AiCliTool> _cache;

        /// <summary>
        /// 설치된 AI CLI 목록을 반환합니다. 결과는 캐시되며 <paramref name="forceRefresh"/>로 다시 검색할 수 있습니다.
        /// </summary>
        /// <param name="forceRefresh">true면 캐시를 무시하고 PATH를 다시 검색합니다.</param>
        public static List<AiCliTool> GetInstalledTools(bool forceRefresh = false)
        {
            if (_cache != null && !forceRefresh)
            {
                return _cache;
            }

            var found = new List<AiCliTool>();
            foreach ((string command, string displayName) in KnownTools)
            {
                string path = ResolveFromPath(command);
                if (!string.IsNullOrEmpty(path))
                {
                    found.Add(new AiCliTool { displayName = displayName, command = command, resolvedPath = path });
                }
            }

            _cache = found;
            return _cache;
        }

        /// <summary>
        /// PATH에서 커맨드의 실행 파일 경로를 찾습니다. Windows는 where.exe, macOS/Linux는 which를 사용합니다.
        /// Windows에서는 PATHEXT에 없는 .ps1 스크립트 CLI(예: Copilot CLI)도 찾도록
        /// `where.exe &lt;name&gt;.ps1` 및 PATH 디렉터리 직접 순회로 보강합니다.
        /// </summary>
        /// <param name="command">커맨드 이름 (예: "claude").</param>
        /// <returns>첫 번째 매칭 경로, 없으면 null.</returns>
        public static string ResolveFromPath(string command)
        {
            bool isWindows = Path.DirectorySeparatorChar == '\\';
            string result = RunLocator(command, isWindows);
            if (!string.IsNullOrEmpty(result))
            {
                return result;
            }

            if (isWindows)
            {
                // where.exe는 PATHEXT 기반이라 .ps1은 못 찾는다 — 확장자를 명시해 재시도.
                result = RunLocator(command + ".ps1", isWindows);
                if (!string.IsNullOrEmpty(result))
                {
                    return result;
                }

                // where.exe 자체가 없는 환경 대비: PATH 디렉터리를 직접 순회.
                result = ScanPathDirectories(command);
            }

            return result;
        }

        /// <summary>PATH 환경변수의 각 디렉터리에서 &lt;name&gt;.exe/.cmd/.bat/.ps1을 직접 찾습니다 (Windows 전용 폴백).</summary>
        private static string ScanPathDirectories(string command)
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] extensions = { ".exe", ".cmd", ".bat", ".ps1" };
            foreach (string dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                foreach (string ext in extensions)
                {
                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), command + ext);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                        // PATH에 잘못된 문자가 포함된 항목 등은 건너뜀
                    }
                }
            }

            return null;
        }

        private static string RunLocator(string command, bool isWindows)
        {
            string locator = isWindows ? "where.exe" : "which";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = locator,
                    Arguments = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    if (process.HasExited && process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        string[] lines = output.Replace("\r", string.Empty)
                            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length == 0)
                        {
                            return null;
                        }

                        if (isWindows)
                        {
                            // where.exe는 확장자 없는 파일(npm이 함께 설치하는 bash 스크립트 등)도 먼저 반환할 수 있다.
                            // Windows에서 직접 실행 가능한 확장자를 우선순위(.exe > .cmd > .bat > .ps1)로 골라낸다.
                            string[] priority = { ".exe", ".cmd", ".bat", ".ps1" };
                            foreach (string ext in priority)
                            {
                                foreach (string line in lines)
                                {
                                    string candidate = line.Trim();
                                    if (candidate.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                                    {
                                        return candidate;
                                    }
                                }
                            }
                        }

                        return lines[0].Trim();
                    }
                }
            }
            catch
            {
                // locator 자체가 없는 환경 — 미설치로 처리
            }

            return null;
        }

        /// <summary>
        /// 선택한 AI CLI에 프롬프트를 전달해 비대화형으로 실행하고 stdout을 캡처합니다.
        /// 프롬프트는 커맨드라인 인자 길이 제한(Windows 약 32K)을 피하기 위해 기본적으로 stdin으로 전달합니다.
        /// </summary>
        /// <param name="command">실행할 CLI 커맨드 이름 (예: "claude") 또는 임의 커맨드라인.</param>
        /// <param name="prompt">전달할 프롬프트 (한국어 포함, UTF-8).</param>
        /// <param name="timeoutSeconds">타임아웃 (초). 0 이하이면 300초.</param>
        /// <param name="cancellation">취소 토큰 (취소 시 프로세스 강제 종료).</param>
        /// <param name="allowReadTools">
        /// true면 CLI별로 헤드리스 모드에서 "파일 읽기 전용" 도구 사용을 허용하는 플래그를 붙입니다
        /// (프로젝트 탐색 모드). 쓰기/셸 실행 도구는 어떤 CLI에서도 허용하지 않습니다.
        /// </param>
        /// <param name="workingDirectory">
        /// 프로세스 작업 디렉터리 (예: Unity 프로젝트 루트). null이면 지정하지 않습니다.
        /// 탐색 모드에서 CLI가 상대 경로(Assets/...)로 프로젝트 파일을 읽을 수 있게 합니다.
        /// </param>
        public static async Task<AiCliResult> RunAsync(
            string command, string prompt, int timeoutSeconds, CancellationToken cancellation,
            bool allowReadTools = false, string workingDirectory = null)
        {
            if (timeoutSeconds <= 0)
            {
                timeoutSeconds = 300;
            }

            // CLI별 비대화형 실행 인자와 프롬프트 전달 방식.
            // 전 CLI 공통으로 stdin 파이프를 우선한다 (인자 길이 제한 회피 + 따옴표 이스케이프 문제 없음).
            // - claude:       `claude -p`                          — -p(print) 모드에서 프롬프트 인자를 생략하면 stdin에서 읽음
            // - codex:        `codex exec -`                       — exec 서브커맨드에 "-"를 주면 stdin에서 프롬프트를 읽음
            // - gemini:       `gemini`                             — stdin이 파이프면 비대화형으로 stdin 프롬프트 처리 (-p는 인자 전달용)
            // - cursor-agent: `cursor-agent -p --output-format text` — -p(print) 모드에서 stdin 파이프 지원
            // - copilot:      `copilot -p <짧은 인자>` + 임시 파일   — stdin 프롬프트 지원이 문서상 불확실해 임시 파일 방식 사용.
            //                 --allow-all-tools는 붙이지 않음(파일 읽기는 기본 허용, 읽기 전용 사용).
            string arguments;
            bool useStdin = true;
            string tempPromptFile = null;
            string tempOutputFile = null; // codex: 최종 응답만 담기는 --output-last-message 파일

            // 읽기 전용 도구 허용 플래그 (allowReadTools=true, 프로젝트 탐색 모드) — CLI별 근거:
            // - claude:       `--allowedTools "Read Glob Grep"` — 로컬 `claude -p --help`로 확인
            //                 (--allowedTools <tools...>: 허용 도구 목록). Read/Glob/Grep 읽기 도구만
            //                 허용하고 Write/Edit/Bash 등은 목록에 없으므로 헤드리스에서 자동 실행 불가.
            // - codex:        추가 플래그 불요 — `codex exec`의 기본 샌드박스가 workspace 읽기를 허용
            //                 (기본값이 읽기 허용/쓰기 승인 필요이므로 그대로 읽기 전용 원칙 충족).
            // - gemini:       추가 플래그 불요 — Gemini CLI 비대화형 모드는 read_file/glob/grep 등
            //                 읽기 전용 내장 도구를 승인 없이 실행하고, 쓰기/셸은 --yolo 없이는 승인
            //                 필요로 실행되지 않음(공식 문서 기반). --yolo는 절대 붙이지 않는다.
            // - cursor-agent: 추가 플래그 불요 — print 모드에서 쓰기 계열은 --force 없이는 수행되지
            //                 않으며, 읽기 도구 허용 전용 플래그는 문서상 불확실해 붙이지 않음
            //                 (읽기가 막혀도 프롬프트 인라인 재료만으로 동작하는 우아한 성능 저하).
            // - copilot:      추가 플래그 불요 — 읽기 도구 허용 방식(--allow-tool 세부 문법)이 문서상
            //                 불확실해 보수적으로 생략. --allow-all-tools는 쓰기/셸까지 열리므로 금지.
            switch (command)
            {
                case "claude":
                    arguments = allowReadTools ? "-p --allowedTools \"Read Glob Grep\"" : "-p";
                    break;
                case "codex":
                    // codex exec는 stdout에 헤더/프롬프트 에코/"tokens used" 등 로그를 섞어 출력하므로
                    // --output-last-message로 최종 응답만 임시 파일에 받아 그것을 결과로 사용한다
                    // (codex exec --help로 -o/--output-last-message 지원 확인, v0.144.6).
                    tempOutputFile = Path.Combine(Path.GetTempPath(), $"mcptools_codex_out_{Guid.NewGuid():N}.txt");
                    arguments = "exec - --output-last-message " + Quote(tempOutputFile);
                    break;
                case "gemini":
                    arguments = string.Empty;
                    break;
                case "cursor-agent":
                    arguments = "-p --output-format text";
                    break;
                case "copilot":
                    useStdin = false;
                    tempPromptFile = Path.Combine(Path.GetTempPath(), $"mcptools_prompt_{Guid.NewGuid():N}.txt");
                    File.WriteAllText(tempPromptFile, prompt, new UTF8Encoding(false));
                    arguments = "-p " + Quote(
                        $"다음 파일의 내용을 프롬프트로 읽고 그대로 수행하세요: {tempPromptFile}");
                    break;
                default:
                    // "직접 입력" 커맨드: 첫 토큰을 실행 파일, 나머지를 인자로 사용하고 stdin으로 프롬프트 전달
                    SplitCommandLine(command, out command, out arguments);
                    break;
            }

            try
            {
                AiCliResult result = await Task.Run(() => RunProcess(command, arguments, useStdin ? prompt : null,
                    timeoutSeconds, cancellation, workingDirectory));

                // codex: 최종 응답 파일이 있으면 로그가 섞인 stdout 대신 그 내용을 사용 (없거나 비면 stdout 폴백).
                if (tempOutputFile != null && result.success)
                {
                    try
                    {
                        if (File.Exists(tempOutputFile))
                        {
                            string lastMessage = File.ReadAllText(tempOutputFile, Encoding.UTF8).Trim();
                            if (lastMessage.Length > 0)
                            {
                                result.stdout = lastMessage;
                            }
                        }
                    }
                    catch
                    {
                        // 파일 읽기 실패 시 stdout 폴백 유지
                    }
                }

                return result;
            }
            finally
            {
                if (tempPromptFile != null)
                {
                    try { File.Delete(tempPromptFile); } catch { /* 임시 파일 정리 실패는 무시 */ }
                }

                if (tempOutputFile != null)
                {
                    try { File.Delete(tempOutputFile); } catch { /* 임시 파일 정리 실패는 무시 */ }
                }
            }
        }

        private static AiCliResult RunProcess(
            string command, string arguments, string stdinText, int timeoutSeconds,
            CancellationToken cancellation, string workingDirectory = null)
        {
            var result = new AiCliResult();
            // "직접 입력" 커맨드가 전체 경로일 수 있으므로 파일이 존재하면 그대로 사용, 아니면 PATH에서 찾는다.
            string resolved = File.Exists(command) ? command : ResolveFromPath(command);
            if (string.IsNullOrEmpty(resolved))
            {
                result.errorMessage = $"'{command}' 명령을 PATH에서 찾을 수 없습니다. 해당 CLI가 설치되어 있는지 확인해주세요.";
                return result;
            }

            string fileName = resolved;
            // Windows에서 스크립트 형태 CLI는 직접 실행할 수 없어 호스트 셸을 경유한다.
            // - .cmd/.bat (npm 계열): cmd.exe /c
            // - .ps1 (Copilot CLI 등): powershell.exe -File
            string ext = Path.GetExtension(resolved).ToLowerInvariant();
            if (ext == ".cmd" || ext == ".bat")
            {
                arguments = $"/d /s /c \"{Quote(resolved)} {arguments}\"";
                fileName = "cmd.exe";
            }
            else if (ext == ".ps1")
            {
                arguments = $"-NoProfile -ExecutionPolicy Bypass -File {Quote(resolved)} {arguments}".TrimEnd();
                fileName = "powershell.exe";
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = stdinText != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, // 한글 프롬프트/응답 대응
                StandardErrorEncoding = Encoding.UTF8
            };

            // 프로젝트 탐색 모드: 상대 경로(Assets/...)로 파일을 읽을 수 있게 작업 디렉터리를 명시한다.
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            {
                psi.WorkingDirectory = workingDirectory;
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            try
            {
                using (var process = new Process { StartInfo = psi })
                {
                    process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                    process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (stdinText != null)
                    {
                        using (var writer = new StreamWriter(
                            process.StandardInput.BaseStream, new UTF8Encoding(false)))
                        {
                            writer.Write(stdinText);
                        }
                    }

                    var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                    while (!process.WaitForExit(200))
                    {
                        if (cancellation.IsCancellationRequested)
                        {
                            TryKill(process);
                            result.canceled = true;
                            result.errorMessage = "사용자가 실행을 취소했습니다.";
                            return Finish(result, stdout, stderr);
                        }

                        if (DateTime.UtcNow > deadline)
                        {
                            TryKill(process);
                            result.timedOut = true;
                            result.errorMessage =
                                $"AI CLI 실행이 {timeoutSeconds}초 안에 끝나지 않아 중단했습니다. " +
                                "타임아웃을 늘리거나 프롬프트를 줄여 다시 시도해주세요.";
                            return Finish(result, stdout, stderr);
                        }
                    }

                    process.WaitForExit(); // 비동기 출력 버퍼 플러시 보장
                    result.exitCode = process.ExitCode;
                    result.success = process.ExitCode == 0;
                    if (!result.success)
                    {
                        result.errorMessage =
                            $"AI CLI가 오류로 종료했습니다 (종료 코드 {process.ExitCode}). " +
                            "CLI 로그인 상태와 네트워크 연결을 확인해주세요.";
                    }
                }
            }
            catch (Exception e)
            {
                result.errorMessage = $"AI CLI 실행에 실패했습니다: {e.Message}";
            }

            return Finish(result, stdout, stderr);
        }

        private static AiCliResult Finish(AiCliResult result, StringBuilder stdout, StringBuilder stderr)
        {
            result.stdout = stdout.ToString().Trim();
            result.stderr = stderr.ToString().Trim();
            return result;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // 이미 종료된 경우 등 무시
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void SplitCommandLine(string commandLine, out string command, out string arguments)
        {
            commandLine = (commandLine ?? string.Empty).Trim();
            if (commandLine.StartsWith("\""))
            {
                int end = commandLine.IndexOf('"', 1);
                if (end > 0)
                {
                    command = commandLine.Substring(1, end - 1);
                    arguments = commandLine.Substring(end + 1).Trim();
                    return;
                }
            }

            int space = commandLine.IndexOf(' ');
            command = space < 0 ? commandLine : commandLine.Substring(0, space);
            arguments = space < 0 ? string.Empty : commandLine.Substring(space + 1).Trim();
        }
    }
}
