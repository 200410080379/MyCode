using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiniLink
{
    /// <summary>
    /// #22: 网络管理器 — 统一管理连接生命周期
    /// 
    /// 核心职责：
    /// 1. 管理连接/断开/重连流程
    /// 2. 场景切换时清理网络状态
    /// 3. 协调各子系统的初始化
    /// 4. 提供网络状态查询
    /// 
    /// 使用方式：在场景中创建一个 GameObject，挂载此组件，
    /// 或调用 NetworkManager.GetOrCreate() 自动创建
    /// </summary>
    [AddComponentMenu("MiniLink/NetworkManager")]
    public class NetworkManager : MonoBehaviour
    {
        #region Singleton

        public static NetworkManager singleton { get; private set; }

        /// <summary>
        /// 获取或创建单例
        /// </summary>
        public static NetworkManager GetOrCreate()
        {
            if (singleton != null) return singleton;

            singleton = FindObjectOfType<NetworkManager>();
            if (singleton != null) return singleton;

            var go = new GameObject("NetworkManager");
            DontDestroyOnLoad(go);
            singleton = go.AddComponent<NetworkManager>();
            Debug.Log("[NetworkManager] 自动创建单例");
            return singleton;
        }

        private void Awake()
        {
            if (singleton != null && singleton != this)
            {
                Destroy(gameObject);
                return;
            }
            singleton = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (singleton == this)
            {
                singleton = null;
            }
        }

        #endregion

        #region Serialized Fields

        [Header("Connection Settings")]
        [Tooltip("默认服务器地址")]
        [SerializeField] private string defaultServerUrl = "ws://localhost:9000";

        [Tooltip("是否自动连接")]
        [SerializeField] private bool autoConnect = false;

        [Tooltip("是否在断线时自动重连")]
        [SerializeField] private bool autoReconnect = true;

        [Header("Debug")]
        [Tooltip("是否在GUI显示网络状态")]
        [SerializeField] private bool showDebugGUI = false;

        #endregion

        #region Properties

        /// <summary>当前网络状态</summary>
        public NetworkState state { get; private set; } = NetworkState.Offline;

        /// <summary>是否已连接</summary>
        public bool isConnected => NetworkClient.isConnected;

        /// <summary>当前服务器地址</summary>
        public string serverUrl { get; private set; }

        /// <summary>最后连接时间</summary>
        public DateTime? lastConnectTime { get; private set; }

        /// <summary>最后断线时间</summary>
        public DateTime? lastDisconnectTime { get; private set; }

        #endregion

        #region Events

        /// <summary>连接成功事件</summary>
        public event Action OnClientConnected;

        /// <summary>断开连接事件</summary>
        public event Action OnClientDisconnected;

        /// <summary>网络错误事件</summary>
        public event Action<string> OnNetworkError;

        #endregion

        #region Unity Lifecycle

        private void Start()
        {
            if (autoConnect && !NetworkClient.isConnected)
            {
                ConnectToServer(defaultServerUrl);
            }
        }

        private void Update()
        {
            UpdateState();
        }

        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!showDebugGUI) return;

            GUILayout.BeginArea(new UnityEngine.Rect(10, 10, 300, 200));
            GUILayout.Label($"<b>MiniLink NetworkManager</b>", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"State: {state}");
            GUILayout.Label($"Connected: {isConnected}");
            GUILayout.Label($"Server: {serverUrl ?? "N/A"}");
            GUILayout.Label($"Ping: {NetworkClient.connection?.pingMs ?? 0}ms");
            GUILayout.Label($"Objects: {NetworkClient.spawnedObjects?.Count ?? 0}");
            GUILayout.Label($"Player: {(NetworkClient.localPlayer != null ? NetworkClient.localPlayer.netId.ToString() : "None")}");
            GUILayout.EndArea();
        }
        #endif

        #endregion

        #region Public API

        /// <summary>
        /// 连接服务器
        /// </summary>
        public void ConnectToServer(string url = null)
        {
            if (NetworkClient.isConnected)
            {
                Debug.LogWarning("[NetworkManager] 已连接，先断开");
                Disconnect();
            }

            serverUrl = url ?? defaultServerUrl;
            state = NetworkState.Connecting;

            Debug.Log($"[NetworkManager] 连接服务器: {serverUrl}");

            // 确保必要的单例存在
            EnsureSingletons();

            // 注册连接回调
            RegisterCallbacks();

            // 发起连接
            NetworkClient.Connect(serverUrl);
            lastConnectTime = DateTime.Now;
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            if (!NetworkClient.isConnected && state == NetworkState.Offline) return;

            Debug.Log("[NetworkManager] 断开连接");
            NetworkClient.Disconnect();
            state = NetworkState.Offline;
            lastDisconnectTime = DateTime.Now;

            OnClientDisconnected?.Invoke();
        }

        /// <summary>
        /// 获取网络统计信息
        /// </summary>
        public NetworkDebugInfo GetDebugInfo()
        {
            return new NetworkDebugInfo
            {
                state = state,
                serverUrl = serverUrl ?? "",
                isConnected = NetworkClient.isConnected,
                pingMs = NetworkClient.connection?.pingMs ?? 0,
                spawnedObjects = NetworkClient.spawnedObjects?.Count ?? 0,
                localPlayerNetId = NetworkClient.localPlayer?.netId ?? 0,
                connectionId = NetworkClient.connectionId ?? "",
                playerId = NetworkClient.playerId ?? "",
            };
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// 确保必要的单例组件存在
        /// </summary>
        private void EnsureSingletons()
        {
            // #29: 确保 ReconnectionManager 存在
            ReconnectionManager.GetOrCreate();
        }

        private bool _callbacksRegistered = false;

        /// <summary>
        /// 注册网络事件回调（只注册一次）
        /// </summary>
        private void RegisterCallbacks()
        {
            if (_callbacksRegistered) return;
            _callbacksRegistered = true;

            // 使用 SceneManager 场景切换事件清理状态
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        /// <summary>
        /// 场景卸载时清理网络状态
        /// </summary>
        private void OnSceneUnloaded(Scene scene)
        {
            // 不自动断开连接，但清理 spawned objects
            // 实际项目中根据需求决定是否断开
            Debug.Log($"[NetworkManager] 场景卸载: {scene.name}");
        }

        /// <summary>
        /// 更新网络状态
        /// </summary>
        private void UpdateState()
        {
            var previousState = state;

            if (NetworkClient.isConnected)
            {
                if (NetworkClient.isAuthenticated)
                {
                    state = NetworkState.Connected;
                }
                else
                {
                    state = NetworkState.Authenticating;
                }
            }
            else if (state != NetworkState.Offline)
            {
                // 检查是否正在重连
                var reconn = ReconnectionManager.singleton;
                if (reconn != null && reconn.state == ReconnectState.Reconnecting)
                {
                    state = NetworkState.Reconnecting;
                }
                else if (reconn != null && reconn.state == ReconnectState.Restoring)
                {
                    state = NetworkState.Restoring;
                }
                else
                {
                    state = NetworkState.Offline;
                }
            }

            // 触发事件
            if (previousState != NetworkState.Connected && state == NetworkState.Connected)
            {
                OnClientConnected?.Invoke();
            }
            else if (previousState == NetworkState.Connected && state != NetworkState.Connected)
            {
                OnClientDisconnected?.Invoke();
            }
        }

        #endregion
    }

    /// <summary>
    /// 网络状态枚举
    /// </summary>
    public enum NetworkState
    {
        Offline,        // 离线
        Connecting,     // 连接中
        Authenticating, // 认证中
        Connected,      // 已连接
        Reconnecting,   // 重连中
        Restoring,      // 恢复状态中
    }

    /// <summary>
    /// 网络调试信息
    /// </summary>
    [Serializable]
    public class NetworkDebugInfo
    {
        public NetworkState state;
        public string serverUrl;
        public bool isConnected;
        public int pingMs;
        public int spawnedObjects;
        public uint localPlayerNetId;
        public string connectionId;
        public string playerId;

        public override string ToString()
        {
            return $"[{state}] {serverUrl} ping={pingMs}ms objects={spawnedObjects} player={localPlayerNetId}";
        }
    }
}
