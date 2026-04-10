using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace EvacSim.Decision.LLM
{
    /// <summary>
    /// LLMサーバー (llm_server/server.py) のプロセスを管理するシングルトン。
    /// シミュレーション開始時にサーバーを自動起動し、終了時に自動停止する。
    /// </summary>
    public class LLMServerManager : MonoBehaviour
    {
        private const string Tag = "[LLMServerManager]";

        [Header("コマンド設定")]
        [SerializeField] private string commandPath = "uv";

        [Header("自動起動")]
        [SerializeField] private bool autoStart = true;

        [Header("サーバー接続設定")]
        [SerializeField] private string serverHost = "127.0.0.1";
        [SerializeField] private int serverPort = 8765;

        [Header("起動待ちタイムアウト (秒)")]
        [SerializeField] private float startupTimeoutSeconds = 30f;

        [Header("接続テストのリトライ間隔 (秒)")]
        [SerializeField] private float retryIntervalSeconds = 1f;

        public static LLMServerManager Instance { get; private set; }

        /// <summary>サーバーが接続可能な状態かどうか</summary>
        public bool IsServerReady { get; private set; }

        private Process _serverProcess;
        private CancellationTokenSource _cts;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private async void OnEnable()
        {
            if (autoStart)
            {
                await StartServerAsync();
            }
        }

        private void OnApplicationQuit()
        {
            _isQuitting = true;
            StopServer();
        }

        private void OnDestroy()
        {
            StopServer();
            if (Instance == this) Instance = null;
        }

        private bool _isQuitting;

        /// <summary>
        /// サーバーを起動し、接続可能になるまで待機する。
        /// </summary>
        public async Task StartServerAsync()
        {
            if (IsServerReady) return;

            // 既にプロセスが生きている場合はスキップ
            if (_serverProcess is { HasExited: false })
            {
                Debug.Log($"{Tag} サーバープロセスは既に起動中です");
                await WaitForServerReady();
                return;
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            var serverScriptPath = ResolveServerScriptPath();
            if (serverScriptPath == null)
            {
                Debug.LogError($"{Tag} server.py が見つかりません");
                return;
            }

            Debug.Log($"{Tag} サーバーを起動します: {commandPath} run {serverScriptPath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = commandPath,
                Arguments = $"run \"{serverScriptPath}\"",
                WorkingDirectory = Path.GetDirectoryName(serverScriptPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // サーバー側の環境変数を設定
            startInfo.EnvironmentVariables["LLM_SERVER_HOST"] = serverHost;
            startInfo.EnvironmentVariables["LLM_SERVER_PORT"] = serverPort.ToString();
            // Python の出力バッファリングを無効化して即時ログ転送
            startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

            try
            {
                _serverProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                _serverProcess.OutputDataReceived += OnStdout;
                _serverProcess.ErrorDataReceived += OnStderr;
                _serverProcess.Exited += OnProcessExited;

                _serverProcess.Start();
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();

                Debug.Log($"{Tag} プロセス起動 (PID: {_serverProcess.Id})");

                await WaitForServerReady();
            }
            catch (Exception ex)
            {
                Debug.LogError($"{Tag} サーバー起動に失敗しました: {ex.Message}");
                IsServerReady = false;
            }
        }

        /// <summary>
        /// サーバーが WebSocket 接続を受け付けるようになるまでリトライする。
        /// </summary>
        private async Task WaitForServerReady()
        {
            var wsUrl = $"ws://{serverHost}:{serverPort}";
            var deadline = Time.realtimeSinceStartup + startupTimeoutSeconds;

            Debug.Log($"{Tag} サーバーの準備完了を待機中... ({wsUrl})");

            while (Time.realtimeSinceStartup < deadline)
            {
                if (_cts == null || _cts.Token.IsCancellationRequested) return;

                // プロセスが既に終了していたら中断
                if (_serverProcess != null && _serverProcess.HasExited)
                {
                    Debug.LogError($"{Tag} サーバープロセスが予期せず終了しました (exit code: {_serverProcess.ExitCode})");
                    return;
                }

                try
                {
                    using var testSocket = new ClientWebSocket();
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await testSocket.ConnectAsync(new Uri(wsUrl), timeoutCts.Token);
                    await testSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ready check", CancellationToken.None);

                    IsServerReady = true;
                    Debug.Log($"{Tag} サーバー起動完了");
                    return;
                }
                catch
                {
                    // まだ起動していない — リトライ
                }

                await Task.Delay(TimeSpan.FromSeconds(retryIntervalSeconds));
            }

            Debug.LogError($"{Tag} サーバーの起動がタイムアウトしました ({startupTimeoutSeconds}秒)");
        }

        /// <summary>
        /// サーバープロセスを停止する。
        /// </summary>
        public void StopServer()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            IsServerReady = false;

            if (_serverProcess == null) return;

            try
            {
                if (!_serverProcess.HasExited)
                {
                    Debug.Log($"{Tag} サーバープロセスを停止します (PID: {_serverProcess.Id})");
                    // 子プロセスも含めて終了させるため、プラットフォーム別に対応
                    KillProcessTree(_serverProcess.Id);
                    _serverProcess.WaitForExit(5000);
                    Debug.Log($"{Tag} サーバープロセスを停止しました");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} サーバー停止時に例外: {ex.Message}");
            }
            finally
            {
                _serverProcess.OutputDataReceived -= OnStdout;
                _serverProcess.ErrorDataReceived -= OnStderr;
                _serverProcess.Exited -= OnProcessExited;
                _serverProcess.Dispose();
                _serverProcess = null;
            }
        }

        /// <summary>
        /// プロセスツリーごと終了させる。
        /// Unity の .NET ランタイムでは Process.Kill(entireProcessTree) が使えないため、
        /// OS コマンドで子プロセスも含めて終了する。
        /// </summary>
        private static void KillProcessTree(int pid)
        {
            try
            {
                var killInfo = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                // Windows: taskkill /T で子プロセスも終了
                killInfo.FileName = "taskkill";
                killInfo.Arguments = $"/PID {pid} /T /F";
#else
                // macOS / Linux: kill でプロセスグループごと終了
                // Python の子プロセスも含めて終了させる
                killInfo.FileName = "kill";
                killInfo.Arguments = $"-TERM -{pid}";
#endif

                using var killProcess = Process.Start(killInfo);
                killProcess?.WaitForExit(3000);
            }
            catch
            {
                // フォールバック: 単体 Kill
                try
                {
                    var proc = Process.GetProcessById(pid);
                    proc.Kill();
                }
                catch
                {
                    // 既に終了済み
                }
            }
        }

        /// <summary>
        /// llm_server/server.py の絶対パスを返す。
        /// Unity Editor ではプロジェクトルートからの相対パスを使用する。
        /// </summary>
        private string ResolveServerScriptPath()
        {
            // Application.dataPath = "<ProjectRoot>/Assets"
            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var candidate = Path.Combine(projectRoot, "llm_server", "server.py");
            return File.Exists(candidate) ? candidate : null;
        }

        private static void OnStdout(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
                Debug.Log($"[LLM Server] {e.Data}");
        }

        private static void OnStderr(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
                Debug.LogWarning($"[LLM Server/err] {e.Data}");
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            IsServerReady = false;

            if (_isQuitting) return;

            Debug.LogWarning($"{Tag} サーバープロセスが予期せず終了しました。自動再起動します...");
            // メインスレッドで再起動をスケジュール
            _restartRequested = true;
        }

        private volatile bool _restartRequested;

        private async void Update()
        {
            if (!_restartRequested) return;
            _restartRequested = false;

            await StartServerAsync();
        }
    }
}
