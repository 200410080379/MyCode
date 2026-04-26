using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 网络客户端核心
    /// 管理连接、消息收发、对象池、同步
    /// </summary>
    public static class NetworkClient
    {
        #region Properties

        /// <summary>当前连接</summary>
        public static NetworkConnection connection { get; internal set; }

        /// <summary>是否已连接</summary>
        public static bool isConnected => connection != null && connection.isConnected;

        /// <summary>是否已认证</summary>
        public static bool isAuthenticated => connection != null && connection.isAuthenticated;

        /// <summary>本地玩家对象</summary>
        public static NetworkIdentity localPlayer { get; internal set; }

        /// <summary>连接ID</summary>
        public static string connectionId => connection?.connectionId;

        /// <summary>玩家ID（握手后默认为clientId，登录后切换为openid）</summary>
        public static string playerId => connection?.playerId;

        #endregion

        #region Internal Fields

        /// <summary>网络对象池 netId -> NetworkIdentity</summary>
        internal static readonly Dictionary<uint, NetworkIdentity> spawnedObjects = new Dictionary<uint, NetworkIdentity>();

        /// <summary>消息处理器</summary>
        private static readonly Dictionary<string, Action<NetworkReader>> messageHandlers = new Dictionary<string, Action<NetworkReader>>();

        /// <summary>RPC方法缓存 methodHash -> delegate</summary>
        private static readonly Dictionary<string, Delegate> rpcHandlers = new Dictionary<string, Delegate>();

        /// <summary>预制体注册表 prefabHash -> prefab</summary>
        private static readonly Dictionary<int, NetworkIdentity> prefabs = new Dictionary<int, NetworkIdentity>();

        /// <summary>传输层</summary>
        private static ITransport transport;

        /// <summary>运行时驱动（心跳/时间同步）</summary>
        private static NetworkClientRuntime runtime;

        #endregion

        #region Connection

        /// <summary>
        /// 连接服务器
        /// </summary>
        public static void Connect(string serverUrl)
        {
            if (isConnected)
            {
                Debug.LogWarning("[MiniLink] 已经连接");
                return;
            }

            DetachTransportHandlers();

            // 创建传输层（根据平台自动选择）
            transport = CreateTransport();
            transport.OnConnected += OnConnected;
            transport.OnDisconnected += OnDisconnected;
            transport.OnDataReceived += OnDataReceived;

            connection = new NetworkConnection();
            connection.serverUrl = serverUrl;
            runtime = GetOrCreateBehaviourComponent<NetworkClientRuntime>("NetworkClientRuntime");
            runtime.ResetTimers();

            Debug.Log($"[MiniLink] 连接中: {serverUrl}");
            transport.Connect(serverUrl);
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public static void Disconnect()
        {
            if (!isConnected) return;

            Debug.Log("[MiniLink] 断开连接");
            transport?.Disconnect();
            Cleanup();
        }

        #endregion

        #region Transport Factory

        /// <summary>
        /// 根据平台创建传输层
        /// </summary>
        private static ITransport CreateTransport()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            var douyinTransport = TtWebSocketTransport.singleton ??
                UnityEngine.Object.FindObjectOfType<TtWebSocketTransport>();
            if (douyinTransport != null)
            {
                return douyinTransport;
            }

            return GetOrCreateTransportComponent<WxWebSocketTransport>("WxWebSocketBridge");
#else
            // 原生环境
            return new WebSocketTransport();
#endif
        }

        private static T GetOrCreateTransportComponent<T>(string objectName)
            where T : MonoBehaviour, ITransport
        {
            var existing = UnityEngine.Object.FindObjectOfType<T>();
            if (existing != null)
            {
                return existing;
            }

            var go = new GameObject(objectName);
            UnityEngine.Object.DontDestroyOnLoad(go);
            return go.AddComponent<T>();
        }

        private static T GetOrCreateBehaviourComponent<T>(string objectName)
            where T : MonoBehaviour
        {
            var existing = UnityEngine.Object.FindObjectOfType<T>();
            if (existing != null)
            {
                return existing;
            }

            var go = new GameObject(objectName);
            UnityEngine.Object.DontDestroyOnLoad(go);
            return go.AddComponent<T>();
        }

        #endregion

        #region Transport Callbacks

        private static void OnConnected()
        {
            Debug.Log("[MiniLink] 连接成功");
            connection.isConnected = true;

            // 发送握手
            SendHandshake();
        }

        private static void OnDisconnected()
        {
            Debug.Log("[MiniLink] 连接断开");
            if (connection != null)
            {
                connection.isConnected = false;
                connection.isAuthenticated = false;
            }

            // 通知所有对象断开
            foreach (var identity in spawnedObjects.Values)
            {
                identity.NotifyDestroy();
            }

            if (ReconnectionManager.singleton != null)
            {
                ReconnectionManager.singleton.OnConnectionLost();
            }
        }

        private static void OnDataReceived(byte[] data)
        {
            try
            {
                // 解析JSON消息
                string json = System.Text.Encoding.UTF8.GetString(data);
                var parsed = MiniJson.Deserialize(json);

                if (parsed is Dictionary<string, object> msg)
                {
                    ProcessMessage(msg);
                }
                else if (parsed is List<object> batch)
                {
                    foreach (var item in batch)
                    {
                        if (item is Dictionary<string, object> batchedMsg)
                        {
                            ProcessMessage(batchedMsg);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MiniLink] 消息解析失败: {ex.Message}");
            }
        }

        #endregion

        #region Message Processing

        private static void ProcessMessage(Dictionary<string, object> msg)
        {
            int msgType = Convert.ToInt32(msg["type"]);

            switch (msgType)
            {
                case 2: // HANDSHAKE_RESP
                    ProcessHandshakeResp(msg);
                    break;

                case 3: // HEARTBEAT
                    ProcessHeartbeat(msg);
                    break;

                case 14: // ROOM_LIST_RESP
                    ProcessRoomList(msg);
                    break;

                case 17: // ROOM_STATE_NOTIFY
                    ProcessRoomState(msg);
                    break;

                case 20: // SYNC_VAR_MSG
                    ProcessSyncVar(msg);
                    break;

                case 22: // SNAPSHOT_MSG
                    ProcessSnapshot(msg);
                    break;

                case 30: // COMMAND (客户端收到的是RPC响应)
                    ProcessCommand(msg);
                    break;

                case 31: // CLIENT_RPC
                    ProcessClientRpc(msg);
                    break;

                case 32: // TARGET_RPC
                    ProcessTargetRpc(msg);
                    break;

                case 40: // SPAWN
                    ProcessSpawn(msg);
                    break;

                case 41: // DESPAWN
                    ProcessDespawn(msg);
                    break;

                case 61: // WX_LOGIN_RESP
                    ProcessWxLoginResp(msg);
                    break;

                case 70: // FRAME_PAUSE
                    ProcessFramePause(msg);
                    break;

                case 71: // FRAME_CONFIRM
                    ProcessFrameConfirm(msg);
                    break;

                case 80: // RECONNECT_STATE
                    ProcessReconnectState(msg);
                    break;

                case 81: // RECONNECT_RESULT
                    ProcessReconnectResult(msg);
                    break;

                case 91: // TIME_SYNC_RESP
                    ProcessTimeSyncResp(msg);
                    break;

                case 99: // ERROR_MSG
                    ProcessError(msg);
                    break;

                default:
                    Debug.LogWarning($"[MiniLink] 未处理的消息类型: {msgType}");
                    break;
            }
        }

        private static void ProcessHandshakeResp(Dictionary<string, object> msg)
        {
            var resp = msg["handshake_resp"] as Dictionary<string, object>;
            if (resp == null) return;

            bool success = Convert.ToBoolean(resp["success"]);
            if (success)
            {
                connection.isAuthenticated = true;
                connection.sessionId = resp["session_id"] as string;
                connection.reconnectToken = GetString(resp, "reconnect_token", connection.sessionId);
                connection.connectionId = GetString(resp, "conn_id", connection.connectionId);
                connection.playerId = GetString(resp, "player_id", connection.playerId);
                runtime?.ResetTimers();
                ReconnectionManager.singleton?.EnableHeartbeat();
                Debug.Log($"[MiniLink] 认证成功, sessionId={connection.sessionId}");
            }
            else
            {
                Debug.LogError("[MiniLink] 认证失败");
            }
        }

        private static void ProcessHeartbeat(Dictionary<string, object> msg)
        {
            var hb = msg["heartbeat"] as Dictionary<string, object>;
            if (hb == null) return;

            int pingMs = Convert.ToInt32(hb["ping_ms"]);
            connection.UpdatePing(pingMs);

            ReconnectionManager.singleton?.OnHeartbeatReceived();
            LagCompensationManager.singleton?.RecordRttSample(pingMs);
        }

        private static void ProcessRoomList(Dictionary<string, object> msg)
        {
            var roomListResp = msg["room_list_resp"] as Dictionary<string, object>;
            if (roomListResp == null) return;

            if (NetworkRoomManager.singleton != null)
            {
                NetworkRoomManager.OnRoomListResponse(roomListResp);
            }
        }

        private static void ProcessSyncVar(Dictionary<string, object> msg)
        {
            var syncMsg = msg["sync_var_msg"] as Dictionary<string, object>;
            if (syncMsg == null) return;

            uint netId = Convert.ToUInt32(syncMsg["net_id"]);
            var identity = GetSpawnedObject(netId);
            if (identity == null)
            {
                Debug.LogWarning($"[MiniLink] 收到未知对象SyncVar: netId={netId}");
                return;
            }

            int dirtyMask = Convert.ToInt32(syncMsg["dirty_mask"]);
            byte[] payload = DecodePayload(syncMsg["payload"]);

            if (payload == null || payload.Length == 0) return;

            ApplyDirtyMask(identity, dirtyMask);

            using (var reader = new NetworkReader(payload))
            {
                identity.DeserializeSyncVars(reader, false);
            }

            identity.ClearDirtyMask((ulong)dirtyMask);
            ClearDirtyMask(identity);
        }

        private static void ProcessSnapshot(Dictionary<string, object> msg)
        {
            var snapMsg = msg["snapshot_msg"] as Dictionary<string, object>;
            if (snapMsg == null) return;

            uint netId = Convert.ToUInt32(snapMsg["net_id"]);
            var identity = GetSpawnedObject(netId);
            if (identity == null) return;

            long remoteTick = Convert.ToInt64(snapMsg["remote_tick"]);
            float remoteTime = Convert.ToSingle(snapMsg["remote_time"]);
            byte[] payload = DecodePayload(snapMsg["state_data"]);

            // 交给 SnapshotInterpolation 处理
            SnapshotInterpolation.AddSnapshot(netId, remoteTick, remoteTime, payload);
        }

        private static void ProcessClientRpc(Dictionary<string, object> msg)
        {
            var rpcMsg = msg["client_rpc_msg"] as Dictionary<string, object>;
            if (rpcMsg == null) return;

            uint netId = Convert.ToUInt32(rpcMsg["net_id"]);
            string methodHash = rpcMsg["method_hash"] as string;
            bool includeOwner = Convert.ToBoolean(rpcMsg["include_owner"]);
            byte[] args = DecodePayload(rpcMsg["args"]);

            var identity = GetSpawnedObject(netId);
            if (identity == null) return;

            // 检查是否排除拥有者
            if (!includeOwner && identity.isOwned) return;

            // 调用RPC方法
            InvokeRpc(identity, methodHash, args);
        }

        private static void ProcessTargetRpc(Dictionary<string, object> msg)
        {
            var rpcMsg = msg["target_rpc_msg"] as Dictionary<string, object>;
            if (rpcMsg == null) return;

            uint netId = Convert.ToUInt32(rpcMsg["net_id"]);
            string methodHash = rpcMsg["method_hash"] as string;
            byte[] args = DecodePayload(rpcMsg["args"]);

            var identity = GetSpawnedObject(netId);
            if (identity == null) return;

            InvokeRpc(identity, methodHash, args);
        }

        private static void ProcessSpawn(Dictionary<string, object> msg)
        {
            var spawnMsg = msg["spawn_msg"] as Dictionary<string, object>;
            if (spawnMsg == null) return;

            uint netId = Convert.ToUInt32(spawnMsg["net_id"]);
            int prefabHash = Convert.ToInt32(spawnMsg["prefab_hash"]);
            string ownerConnId = spawnMsg["owner_conn_id"] as string;
            string stateBase64 = spawnMsg["initial_state"] as string;
            byte[] initialState = DecodePayload(stateBase64);

            SpawnObject(netId, prefabHash, ownerConnId, initialState);
        }

        private static void ProcessDespawn(Dictionary<string, object> msg)
        {
            var despawnMsg = msg["despawn_msg"] as Dictionary<string, object>;
            if (despawnMsg == null) return;

            uint netId = Convert.ToUInt32(despawnMsg["net_id"]);
            DestroyObject(netId);
        }

        private static void ProcessError(Dictionary<string, object> msg)
        {
            var errMsg = msg["error_msg"] as Dictionary<string, object>;
            if (errMsg == null) return;

            string code = errMsg["code"] as string;
            string message = errMsg["message"] as string;
            Debug.LogError($"[MiniLink] 服务器错误: {code} - {message}");
            NetworkRoomManager.OnServerError(string.IsNullOrEmpty(message) ? code : message);
        }

        private static void ProcessRoomState(Dictionary<string, object> msg)
        {
            var roomMsg = msg["room_state_notify"] as Dictionary<string, object>;
            if (roomMsg == null) return;
            
            // 检查 singleton 是否存在，避免 NullReferenceException
            if (NetworkRoomManager.singleton != null)
            {
                NetworkRoomManager.OnRoomStateUpdate(roomMsg);
            }
            else
            {
                Debug.LogWarning("[MiniLink] NetworkRoomManager.singleton 未初始化，无法处理房间状态");
            }
        }

        private static void ProcessCommand(Dictionary<string, object> msg)
        {
            // 客户端收到的Command通常是服务端的响应
            // 这里简化处理，实际可扩展为双向RPC
        }

        private static void ProcessWxLoginResp(Dictionary<string, object> msg)
        {
            var loginResp = msg["wx_login_resp"] as Dictionary<string, object>;
            if (loginResp == null) return;

            bool success = loginResp.TryGetValue("success", out var successObj) &&
                Convert.ToBoolean(successObj);

            if (success)
            {
                string token = GetString(loginResp, "token");
                string openid = GetString(loginResp, "openid");
                string sessionKey = GetString(loginResp, "session_key");

                connection.isAuthenticated = true;
                connection.playerId = openid;

                if (WechatLogin.singleton != null)
                {
                    WechatLogin.singleton.OnServerLoginSuccess(token, openid, sessionKey);
                }
                else if (DouyinLogin.singleton != null)
                {
                    DouyinLogin.singleton.OnServerLoginSuccess(token, openid, sessionKey);
                }
            }
            else
            {
                connection.isAuthenticated = false;
                const string fallbackMessage = "登录失败";
                if (WechatLogin.singleton != null)
                {
                    WechatLogin.singleton.HandleLoginFailed(fallbackMessage);
                }
                else if (DouyinLogin.singleton != null)
                {
                    DouyinLogin.singleton.HandleLoginFailed(fallbackMessage);
                }
            }
        }

        private static void ProcessFramePause(Dictionary<string, object> msg)
        {
            var pauseMsg = msg["frame_pause_msg"] as Dictionary<string, object>;
            if (pauseMsg == null || FrameSyncManager.singleton == null) return;

            bool paused = pauseMsg.TryGetValue("paused", out var pausedObj) &&
                Convert.ToBoolean(pausedObj);
            FrameSyncManager.singleton.SetPaused(paused);
        }

        private static void ProcessFrameConfirm(Dictionary<string, object> msg)
        {
            var confirmMsg = msg["frame_confirm_msg"] as Dictionary<string, object>;
            if (confirmMsg == null || FrameSyncManager.singleton == null) return;

            long frame = confirmMsg.TryGetValue("frame", out var frameObj)
                ? Convert.ToInt64(frameObj)
                : 0;

            var inputs = new Dictionary<uint, byte[]>();
            if (confirmMsg.TryGetValue("inputs", out var inputsObj) && inputsObj is List<object> inputList)
            {
                foreach (var entry in inputList)
                {
                    if (entry is Dictionary<string, object> inputDict)
                    {
                        uint netId = inputDict.TryGetValue("net_id", out var netIdObj)
                            ? Convert.ToUInt32(netIdObj)
                            : 0;
                        byte[] inputData = DecodePayload(
                            inputDict.TryGetValue("input_data", out var inputObj) ? inputObj : null
                        );
                        inputs[netId] = inputData ?? Array.Empty<byte>();
                    }
                }
            }

            FrameSyncManager.singleton.OnFrameConfirm(frame, inputs);
        }

        private static void ProcessReconnectState(Dictionary<string, object> msg)
        {
            ReconnectionManager.singleton?.OnReconnectState(msg);
        }

        private static void ProcessReconnectResult(Dictionary<string, object> msg)
        {
            ReconnectionManager.singleton?.OnReconnectResult(msg);
        }

        private static void ProcessTimeSyncResp(Dictionary<string, object> msg)
        {
            var timeSyncResp = msg["time_sync_resp"] as Dictionary<string, object>;
            if (timeSyncResp == null || LagCompensationManager.singleton == null) return;

            long clientSendTime = timeSyncResp.TryGetValue("client_send_time", out var sendObj)
                ? Convert.ToInt64(sendObj)
                : 0;
            long serverReceiveTime = timeSyncResp.TryGetValue("server_receive_time", out var receiveObj)
                ? Convert.ToInt64(receiveObj)
                : 0;
            long serverSendTime = timeSyncResp.TryGetValue("server_send_time", out var serverSendObj)
                ? Convert.ToInt64(serverSendObj)
                : 0;

            LagCompensationManager.singleton.RecordTimestampSample(
                clientSendTime,
                serverReceiveTime,
                serverSendTime,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            );
        }

        #endregion

        #region Object Management

        /// <summary>
        /// 注册预制体
        /// </summary>
        public static void RegisterPrefab(NetworkIdentity prefab)
        {
            if (prefab == null) return;
            prefabs[prefab.PrefabHash] = prefab;
        }

        /// <summary>
        /// 生成网络对象
        /// </summary>
        internal static void SpawnObject(uint netId, int prefabHash, string ownerConnId, byte[] initialState)
        {
            if (spawnedObjects.ContainsKey(netId))
            {
                Debug.LogWarning($"[MiniLink] 对象已存在: netId={netId}");
                return;
            }

            GameObject go;
            NetworkIdentity identity;
            if (!prefabs.TryGetValue(prefabHash, out var prefab))
            {
                go = new GameObject($"MiniLink_NetworkObject_{netId}");
                identity = go.AddComponent<NetworkIdentity>();
                Debug.LogWarning($"[MiniLink] 未注册的预制体: hash={prefabHash}，已创建占位对象");
            }
            else
            {
                go = UnityEngine.Object.Instantiate(prefab.gameObject);
                identity = go.GetComponent<NetworkIdentity>();
            }

            identity.netId = netId;
            identity.ownerConnId = ownerConnId;
            identity.isClient = true;
            identity.isServer = false;
            identity.isLocalPlayer = (ownerConnId == connectionId);

            if (identity.isLocalPlayer)
            {
                localPlayer = identity;
            }

            // 反序列化初始状态
            if (initialState != null && initialState.Length > 0)
            {
                using (var reader = new NetworkReader(initialState))
                {
                    identity.DeserializeSyncVars(reader, true);
                }
            }

            // 注册到对象池
            spawnedObjects[netId] = identity;

            // 通知生命周期
            identity.NotifySpawned();

            Debug.Log($"[MiniLink] 生成对象: netId={netId}, prefab={prefabHash}");
        }

        /// <summary>
        /// 销毁网络对象
        /// </summary>
        internal static void DestroyObject(uint netId)
        {
            if (!spawnedObjects.TryGetValue(netId, out var identity))
            {
                return;
            }

            identity.NotifyDestroy();
            spawnedObjects.Remove(netId);

            if (localPlayer == identity)
            {
                localPlayer = null;
            }

            UnityEngine.Object.Destroy(identity.gameObject);
        }

        /// <summary>
        /// 获取生成的对象
        /// </summary>
        public static NetworkIdentity GetSpawnedObject(uint netId)
        {
            spawnedObjects.TryGetValue(netId, out var identity);
            return identity;
        }

        /// <summary>
        /// 注销对象（对象销毁时调用）
        /// </summary>
        internal static void Unspawn(uint netId)
        {
            spawnedObjects.Remove(netId);
        }

        #endregion

        #region Send Methods

        /// <summary>
        /// 发送握手消息
        /// </summary>
        private static void SendHandshake()
        {
            string clientId = SystemInfo.deviceUniqueIdentifier;
            connection.playerId = clientId;

            var msg = new Dictionary<string, object>
            {
                ["type"] = 1,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["handshake_req"] = new Dictionary<string, object>
                {
                    ["client_id"] = clientId,
                    ["version"] = "1.0.0",
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 发送心跳
        /// </summary>
        public static void SendHeartbeat()
        {
            if (!isConnected) return;

            var msg = new Dictionary<string, object>
            {
                ["type"] = 3,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["heartbeat"] = new Dictionary<string, object>
                {
                    ["client_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["ping_ms"] = connection.pingMs,
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 发送Command
        /// </summary>
        public static void SendCommand(CommandMessage cmd)
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = 30,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["command_msg"] = new Dictionary<string, object>
                {
                    ["net_id"] = cmd.netId,
                    ["method_hash"] = cmd.methodHash,
                    ["channel"] = cmd.channel,
                    ["requires_authority"] = cmd.requiresAuthority,
                    ["args"] = Convert.ToBase64String(cmd.args),
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 发送ClientRpc（简化版，实际服务端调用）
        /// </summary>
        public static void SendClientRpc(ClientRpcMessage rpc)
        {
            // 客户端一般不主动发送ClientRpc，这里为测试用
            var msg = new Dictionary<string, object>
            {
                ["type"] = 31,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["client_rpc_msg"] = new Dictionary<string, object>
                {
                    ["net_id"] = rpc.netId,
                    ["method_hash"] = rpc.methodHash,
                    ["channel"] = rpc.channel,
                    ["include_owner"] = rpc.includeOwner,
                    ["args"] = Convert.ToBase64String(rpc.args),
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 发送TargetRpc
        /// </summary>
        public static void SendTargetRpc(TargetRpcMessage rpc)
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = 32,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["target_rpc_msg"] = new Dictionary<string, object>
                {
                    ["net_id"] = rpc.netId,
                    ["method_hash"] = rpc.methodHash,
                    ["target_conn_id"] = rpc.targetConnId,
                    ["channel"] = rpc.channel,
                    ["args"] = Convert.ToBase64String(rpc.args),
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 发送SyncVar更新
        /// </summary>
        public static void SendSyncVar(uint netId, string component, int dirtyMask, byte[] payload)
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = 20,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["sync_var_msg"] = new Dictionary<string, object>
                {
                    ["net_id"] = netId,
                    ["component"] = component,
                    ["dirty_mask"] = dirtyMask,
                    ["payload"] = Convert.ToBase64String(payload),
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 发送输入帧
        /// </summary>
        public static void SendInputFrame(uint netId, int frame, byte[] inputData)
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = 23,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["input_frame_msg"] = new Dictionary<string, object>
                {
                    ["net_id"] = netId,
                    ["frame"] = frame,
                    ["input_data"] = Convert.ToBase64String(inputData),
                }
            };

            SendJson(msg);
        }

        /// <summary>
        /// 重连服务器
        /// </summary>
        public static void Reconnect(string serverUrl)
        {
            // 先断开现有连接
            if (isConnected)
            {
                Disconnect();
            }

            // 连接新地址
            Connect(serverUrl);
        }

        /// <summary>
        /// 发送时间同步请求（用于延迟补偿）
        /// </summary>
        public static void SendTimeSync()
        {
            if (!isConnected) return;

            var msg = new Dictionary<string, object>
            {
                ["type"] = 90,
                ["seq"] = connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["time_sync_req"] = new Dictionary<string, object>
                {
                    ["client_send_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            };

            SendJson(msg);
        }

        public static void SendJson(Dictionary<string, object> msg)
        {
            string json = MiniJson.Serialize(msg);
            byte[] data = System.Text.Encoding.UTF8.GetBytes(json);
            transport?.Send(data);
        }

        #endregion

        #region RPC Invocation

        /// <summary>
        /// 注册RPC处理器
        /// </summary>
        public static void RegisterRpcHandler(string methodHash, Delegate handler)
        {
            rpcHandlers[methodHash] = handler;
        }

        /// <summary>
        /// #3: RPC 调用分发 — 完整实现
        /// 根据 methodHash 查找注册的 handler 并实际调用
        /// 支持无参、单参、双参、三参、四参的 Action 委托
        /// </summary>
        private static void InvokeRpc(NetworkIdentity identity, string methodHash, byte[] args)
        {
            if (rpcHandlers.TryGetValue(methodHash, out var handler))
            {
                try
                {
                    // 解析参数
                    object[] parsedArgs = ParseRpcArgs(args);

                    // 根据委托类型动态调用
                    switch (handler)
                    {
                        case Action action when parsedArgs.Length == 0:
                            action();
                            break;
                        case Action<NetworkReader> readerAction:
                            // 最常用模式：直接传 NetworkReader 让业务层自己读
                            using (var reader = new NetworkReader(args))
                            {
                                readerAction(reader);
                            }
                            break;
                        default:
                            // 尝试 DynamicInvoke
                            handler.DynamicInvoke(parsedArgs);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[MiniLink] RPC调用异常: {methodHash}, {ex.Message}");
                }
            }
            else
            {
                // 没有注册的 handler，通知 NetworkBehaviour 尝试匹配
                if (identity.networkBehaviours != null)
                {
                    foreach (var nb in identity.networkBehaviours)
                    {
                        if (nb != null && nb.TryInvokeRpc(methodHash, args))
                            return;
                    }
                }
                Debug.LogWarning($"[MiniLink] 未注册的RPC: {methodHash}");
            }
        }

        /// <summary>
        /// 解析 RPC 参数字节流
        /// </summary>
        private static object[] ParseRpcArgs(byte[] args)
        {
            if (args == null || args.Length == 0) return Array.Empty<object>();

            var result = new List<object>();
            using (var reader = new NetworkReader(args))
            {
                while (reader.Position < reader.Length)
                {
                    byte typeCode = reader.ReadByte();
                    switch (typeCode)
                    {
                        case 0: result.Add(null); break;
                        case 1: result.Add(reader.ReadInt()); break;
                        case 2: result.Add(reader.ReadFloat()); break;
                        case 3: result.Add(reader.ReadBool()); break;
                        case 4: result.Add(reader.ReadString()); break;
                        case 5: result.Add(reader.ReadVector3()); break;
                        case 6: result.Add(reader.ReadVector2()); break;
                        case 7: result.Add(reader.ReadQuaternion()); break;
                        case 8: result.Add(reader.ReadBytes()); break;
                        default:
                            Debug.LogWarning($"[MiniLink] 未知RPC参数类型: {typeCode}");
                            return result.ToArray();
                    }
                }
            }
            return result.ToArray();
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// #7: 清理时重置所有 static 状态，防止脏数据残留
        /// </summary>
        private static void Cleanup()
        {
            spawnedObjects.Clear();
            localPlayer = null;

            // #7: 清理 RPC 和消息处理器，避免跨场景残留
            rpcHandlers.Clear();
            messageHandlers.Clear();
            prefabs.Clear();

            // #7: 清理插值缓冲
            SnapshotInterpolation.Clear();

            DetachTransportHandlers();
            transport = null;
            runtime?.ResetTimers();

            connection = null;
        }

        private static void DetachTransportHandlers()
        {
            if (transport == null) return;

            transport.OnConnected -= OnConnected;
            transport.OnDisconnected -= OnDisconnected;
            transport.OnDataReceived -= OnDataReceived;
        }

        private static void ApplyDirtyMask(NetworkIdentity identity, int dirtyMask)
        {
            if (identity.networkBehaviours == null) return;

            foreach (var behaviour in identity.networkBehaviours)
            {
                if (behaviour != null)
                {
                    behaviour.componentDirtyMask = (ulong)dirtyMask;
                }
            }
        }

        private static void ClearDirtyMask(NetworkIdentity identity)
        {
            if (identity.networkBehaviours == null) return;

            foreach (var behaviour in identity.networkBehaviours)
            {
                if (behaviour != null)
                {
                    behaviour.componentDirtyMask = 0;
                }
            }
        }

        private static byte[] DecodePayload(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is string stringValue)
            {
                if (string.IsNullOrEmpty(stringValue))
                {
                    return null;
                }

                try
                {
                    return Convert.FromBase64String(stringValue);
                }
                catch (FormatException)
                {
                    Debug.LogWarning("[MiniLink] 收到非Base64编码数据，已按UTF8处理");
                    return System.Text.Encoding.UTF8.GetBytes(stringValue);
                }
            }

            if (value is Dictionary<string, object> dict &&
                dict.TryGetValue("type", out var typeObj) &&
                typeObj?.ToString() == "Buffer" &&
                dict.TryGetValue("data", out var dataObj) &&
                dataObj is List<object> dataList)
            {
                var bytes = new byte[dataList.Count];
                for (int i = 0; i < dataList.Count; i++)
                {
                    bytes[i] = Convert.ToByte(dataList[i]);
                }
                return bytes;
            }

            return System.Text.Encoding.UTF8.GetBytes(value.ToString());
        }

        private static string GetString(Dictionary<string, object> dict, string key, string fallback = "")
        {
            return dict != null && dict.TryGetValue(key, out var value) && value != null
                ? value.ToString()
                : fallback;
        }

        #endregion
    }

    /// <summary>
    /// 网络连接状态
    /// </summary>
    public class NetworkConnection
    {
        public string connectionId { get; set; }
        public string serverUrl { get; set; }
        public string sessionId { get; set; }
        public string reconnectToken { get; set; }
        public string playerId { get; set; }
        public bool isConnected { get; set; }
        public bool isAuthenticated { get; set; }
        public int pingMs { get; private set; }

        private int seqCounter = 0;

        public int nextSeq()
        {
            return ++seqCounter;
        }

        public void UpdatePing(int ping)
        {
            pingMs = ping;
        }
    }

    /// <summary>
    /// 传输层接口
    /// </summary>
    public interface ITransport
    {
        event Action OnConnected;
        event Action OnDisconnected;
        event Action<byte[]> OnDataReceived;

        void Connect(string url);
        void Disconnect();
        void Send(byte[] data);
    }

    public class NetworkClientRuntime : MonoBehaviour
    {
        [SerializeField] private float heartbeatInterval = 2f;
        [SerializeField] private float timeSyncInterval = 10f;

        private float heartbeatTimer;
        private float timeSyncTimer;

        public void ResetTimers()
        {
            heartbeatTimer = 0f;
            timeSyncTimer = 0f;
        }

        private void Update()
        {
            if (!NetworkClient.isConnected) return;

            heartbeatTimer += Time.unscaledDeltaTime;
            timeSyncTimer += Time.unscaledDeltaTime;

            if (heartbeatTimer >= heartbeatInterval)
            {
                heartbeatTimer = 0f;
                NetworkClient.SendHeartbeat();
            }

            if (timeSyncTimer >= timeSyncInterval)
            {
                timeSyncTimer = 0f;
                NetworkClient.SendTimeSync();
            }
        }
    }
}
