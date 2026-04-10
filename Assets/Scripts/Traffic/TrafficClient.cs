using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace EvacSim.Traffic
{
    /// <summary>
    /// 交通シミュレーションサーバーとのWebSocket通信を管理するクライアント。
    /// 車両の位置更新を受信し、車両スポーン/ルート変更コマンドを送信する。
    /// </summary>
    public class TrafficClient : MonoBehaviour
    {
        private const string Tag = "[TrafficClient]";

        [Header("接続設定")]
        [SerializeField] private string serverUrl = "ws://localhost:8766";
        [SerializeField] private float requestTimeoutSeconds = 5f;

        [Header("シミュレーション設定")]
        [Tooltip("SUMOのシミュレーションステップを自動実行する")]
        [SerializeField] private bool autoStep = true;
        [Tooltip("シミュレーションステップの実行間隔（秒）")]
        [SerializeField] private float stepInterval = 0.1f;

        public static TrafficClient Instance { get; private set; }

        /// <summary>接続済みフラグ</summary>
        public bool IsConnected => _socket is { State: WebSocketState.Open };

        /// <summary>最新の車両状態一覧</summary>
        public IReadOnlyList<VehicleUpdate> LatestVehicleStates => _latestVehicleStates;

        /// <summary>最新のエッジ交通状態</summary>
        public IReadOnlyDictionary<string, EdgeTrafficUpdate> LatestEdgeTraffic => _latestEdgeTraffic;

        /// <summary>SUMOシミュレーション時間</summary>
        public float SimulationTime { get; private set; }

        /// <summary>車両状態が更新されたときのイベント</summary>
        public event Action<List<VehicleUpdate>> OnVehicleStatesUpdated;

        /// <summary>交通状態が更新されたときのイベント</summary>
        public event Action<Dictionary<string, EdgeTrafficUpdate>> OnTrafficStateUpdated;

        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<TrafficServerResponse>> _pendingRequests = new();
        private Task _receiveLoopTask;

        private List<VehicleUpdate> _latestVehicleStates = new List<VehicleUpdate>();
        private Dictionary<string, EdgeTrafficUpdate> _latestEdgeTraffic = new Dictionary<string, EdgeTrafficUpdate>();
        private float _lastStepTime;

        // バックグラウンドスレッドからメインスレッドへのディスパッチ用キュー
        private readonly System.Collections.Concurrent.ConcurrentQueue<List<VehicleUpdate>> _vehicleUpdateQueue = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<Dictionary<string, EdgeTrafficUpdate>> _trafficUpdateQueue = new();
        private int _requestCounter;

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
            await ConnectAsync();
        }

        private async void OnDisable()
        {
            await DisconnectAsync();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            // バックグラウンドスレッドからのイベントをメインスレッドで発火
            while (_vehicleUpdateQueue.TryDequeue(out var vehicleUpdates))
            {
                OnVehicleStatesUpdated?.Invoke(vehicleUpdates);
            }
            while (_trafficUpdateQueue.TryDequeue(out var trafficUpdates))
            {
                OnTrafficStateUpdated?.Invoke(trafficUpdates);
            }

            if (!autoStep || !IsConnected) return;

            if (Time.time - _lastStepTime >= stepInterval)
            {
                _lastStepTime = Time.time;
                _ = StepAsync();
            }
        }

        /// <summary>サーバーに接続する</summary>
        public async Task ConnectAsync()
        {
            // TrafficServerManagerの準備完了を待機
            await WaitForServerManagerReady();

            const int maxRetries = 5;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (_socket is { State: WebSocketState.Open }) return;

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _socket?.Dispose();
                _socket = new ClientWebSocket();

                try
                {
                    await _socket.ConnectAsync(new Uri(serverUrl), _cts.Token);
                    _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
                    Debug.Log($"{Tag} サーバーに接続しました: {serverUrl}");
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt >= maxRetries)
                    {
                        Debug.LogError($"{Tag} 接続に失敗しました ({attempt}/{maxRetries}): {ex.Message}");
                        return;
                    }
                    Debug.LogWarning($"{Tag} 接続リトライ ({attempt}/{maxRetries}): {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        }

        /// <summary>サーバーから切断する</summary>
        public async Task DisconnectAsync()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            if (_socket != null)
            {
                try
                {
                    if (_socket.State == WebSocketState.Open)
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
                }
                catch { }
                finally
                {
                    _socket.Dispose();
                    _socket = null;
                }
            }
        }

        /// <summary>シミュレーションを1ステップ進める</summary>
        public async Task StepAsync()
        {
            var request = new StepRequest
            {
                request_id = GenerateRequestId(),
            };
            await SendRequestAsync(JsonUtility.ToJson(request), request.request_id);
        }

        /// <summary>車両をスポーンする</summary>
        public async Task<bool> SpawnVehicleAsync(string vehicleId, string originEdge, string destEdge, float departSpeed = 0f)
        {
            var request = new VehicleSpawnRequest
            {
                request_id = GenerateRequestId(),
                spawn_vehicle = new SpawnVehicleData
                {
                    vehicle_id = vehicleId,
                    origin_edge = originEdge,
                    destination_edge = destEdge,
                    depart_speed = departSpeed,
                },
            };

            var response = await SendRequestAsync(JsonUtility.ToJson(request), request.request_id);
            return response != null && string.IsNullOrEmpty(response.error);
        }

        /// <summary>Unity座標ベースで車両をスポーンする（edge解決はサーバー側）</summary>
        public async Task<bool> SpawnVehicleByPositionAsync(string vehicleId, Vector3 originPos, Vector3 destPos, float departSpeed = 0f)
        {
            var request = new VehicleSpawnRequest
            {
                request_id = GenerateRequestId(),
                spawn_vehicle = new SpawnVehicleData
                {
                    vehicle_id = vehicleId,
                    origin_edge = "",  // 空 → サーバーがorigin_posから自動解決
                    destination_edge = "",
                    origin_pos = new LLM.Vector3Payload(originPos),
                    destination_pos = new LLM.Vector3Payload(destPos),
                    depart_speed = departSpeed,
                },
            };

            var response = await SendRequestAsync(JsonUtility.ToJson(request), request.request_id);
            return response != null && string.IsNullOrEmpty(response.error);
        }

        /// <summary>車両を削除する</summary>
        public async Task RemoveVehicleAsync(string vehicleId)
        {
            var request = new VehicleRemoveRequest
            {
                request_id = GenerateRequestId(),
                remove_vehicle = new RemoveVehicleData { vehicle_id = vehicleId },
            };
            await SendRequestAsync(JsonUtility.ToJson(request), request.request_id);
        }

        /// <summary>車両のルートを変更する</summary>
        public async Task SetRouteAsync(string vehicleId, string[] edgeIds)
        {
            var request = new RouteChangeRequest
            {
                request_id = GenerateRequestId(),
                set_route = new SetRouteData
                {
                    vehicle_id = vehicleId,
                    edge_ids = edgeIds,
                },
            };
            await SendRequestAsync(JsonUtility.ToJson(request), request.request_id);
        }

        /// <summary>指定エッジの交通状態を取得する</summary>
        public async Task<Dictionary<string, EdgeTrafficUpdate>> GetTrafficStateAsync(string[] edgeIds = null)
        {
            var request = new TrafficStateRequest
            {
                request_id = GenerateRequestId(),
                edge_ids = edgeIds,
            };

            var response = await SendRequestAsync(JsonUtility.ToJson(request), request.request_id);
            if (response?.edge_traffic != null)
            {
                var result = new Dictionary<string, EdgeTrafficUpdate>();
                foreach (var et in response.edge_traffic)
                    result[et.edge_id] = et;
                return result;
            }
            return new Dictionary<string, EdgeTrafficUpdate>();
        }

        private async Task<TrafficServerResponse> SendRequestAsync(string json, string requestId)
        {
            if (!IsConnected) return null;

            var tcs = new TaskCompletionSource<TrafficServerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = tcs;

            try
            {
                var buffer = Encoding.UTF8.GetBytes(json);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(requestTimeoutSeconds));

                await _socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, timeoutCts.Token);

                using (timeoutCts.Token.Register(() => tcs.TrySetCanceled()))
                {
                    return await tcs.Task;
                }
            }
            catch (OperationCanceledException)
            {
                // Play停止時やタイムアウトによるキャンセルは正常動作 — ログ抑制
                _pendingRequests.TryRemove(requestId, out _);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} リクエスト送信エラー: {ex.Message}");
                _pendingRequests.TryRemove(requestId, out _);
                return null;
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[65536];

            try
            {
                while (!token.IsCancellationRequested && _socket is { State: WebSocketState.Open })
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    var message = sb.ToString();
                    var response = JsonUtility.FromJson<TrafficServerResponse>(message);

                    if (response == null) continue;

                    // 車両状態の更新（メインスレッドキューに投入）
                    if (response.vehicle_states != null && response.vehicle_states.Length > 0)
                    {
                        _latestVehicleStates = new List<VehicleUpdate>(response.vehicle_states);
                        SimulationTime = response.sim_time;
                        _vehicleUpdateQueue.Enqueue(_latestVehicleStates);
                    }

                    // 交通状態の更新（メインスレッドキューに投入）
                    if (response.edge_traffic != null && response.edge_traffic.Length > 0)
                    {
                        var trafficDict = new Dictionary<string, EdgeTrafficUpdate>();
                        foreach (var et in response.edge_traffic)
                            trafficDict[et.edge_id] = et;
                        _latestEdgeTraffic = trafficDict;
                        _trafficUpdateQueue.Enqueue(trafficDict);
                    }

                    // ペンディングリクエストの解決
                    if (!string.IsNullOrEmpty(response.request_id) &&
                        _pendingRequests.TryRemove(response.request_id, out var tcs))
                    {
                        tcs.TrySetResult(response);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} 受信ループエラー: {ex.Message}");
            }
        }

        private async Task WaitForServerManagerReady()
        {
            if (TrafficServerManager.Instance == null)
            {
                var deadline = Time.realtimeSinceStartup + 3f;
                while (TrafficServerManager.Instance == null && Time.realtimeSinceStartup < deadline)
                    await Task.Delay(100);
            }

            var manager = TrafficServerManager.Instance;
            if (manager == null || manager.IsServerReady) return;

            // autoStart=falseの場合は外部起動済みと見なし、待機をスキップ
            if (!manager.AutoStart)
            {
                Debug.Log($"{Tag} 交通サーバーは外部起動モード（autoStart=false）。待機をスキップして直接接続します。");
                return;
            }

            Debug.Log($"{Tag} 交通サーバーの起動完了を待機中...");
            var timeout = Time.realtimeSinceStartup + 60f;
            while (!manager.IsServerReady && Time.realtimeSinceStartup < timeout)
                await Task.Delay(500);

            if (!manager.IsServerReady)
                Debug.LogError($"{Tag} 交通サーバーの起動待機がタイムアウトしました");
        }

        private string GenerateRequestId()
        {
            return $"traffic-{Interlocked.Increment(ref _requestCounter):D6}";
        }
    }
}
