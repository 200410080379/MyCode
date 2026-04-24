using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace MiniLink
{
    /// <summary>
    /// 房间管理器 - 客户端侧
    /// 参考 Mirror NetworkRoomManager 设计
    /// 管理房间创建、加入、准备、开始等流程
    /// </summary>
    [AddComponentMenu("MiniLink/NetworkRoomManager")]
    public class NetworkRoomManager : MonoBehaviour
    {
        #region Singleton

        public static NetworkRoomManager singleton { get; private set; }

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

        #endregion

        #region Serialized Fields

        [Header("Room Settings")]
        [Tooltip("房间玩家预制体")]
        [SerializeField] private NetworkIdentity roomPlayerPrefab;

        [Tooltip("游戏玩家预制体")]
        [SerializeField] private NetworkIdentity gamePlayerPrefab;

        [Tooltip("最小玩家数")]
        [SerializeField] private int minPlayers = 2;

        [Tooltip("最大玩家数")]
        [SerializeField] private int maxPlayers = 4;

        [Header("Scene Settings")]
        [Tooltip("房间场景名")]
        [SerializeField] private string roomScene = "Room";

        [Tooltip("游戏场景名")]
        [SerializeField] private string gameplayScene = "Game";

        #endregion

        #region Properties

        /// <summary>当前房间状态</summary>
        public RoomState CurrentRoom { get; private set; }

        /// <summary>当前房间ID</summary>
        public string CurrentRoomId => CurrentRoom?.roomId;

        /// <summary>是否在房间中</summary>
        public bool isInRoom => CurrentRoom != null;

        /// <summary>是否是房主</summary>
        public bool isHost => CurrentRoom?.hostId == NetworkClient.connectionId;

        /// <summary>房间玩家列表</summary>
        public List<RoomPlayerInfo> Players { get; private set; } = new List<RoomPlayerInfo>();

        #endregion

        #region Events

        [Header("Events")]
        public UnityEvent OnRoomCreated;
        public UnityEvent OnRoomJoined;
        public UnityEvent OnRoomLeft;
        public UnityEvent<RoomState> OnRoomUpdated;
        public UnityEvent<string> OnRoomError;
        public UnityEvent OnGameStarting;
        public UnityEvent OnGameStarted;

        #endregion

        #region Room Actions

        /// <summary>
        /// 创建房间
        /// </summary>
        public void CreateRoom(string roomName = "", string password = "", int maxPlayers = 0)
        {
            if (isInRoom)
            {
                OnRoomError?.Invoke("已在房间中");
                return;
            }

            var msg = new Dictionary<string, object>
            {
                ["type"] = 10,
                ["seq"] = NetworkClient.connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["room_create_req"] = new Dictionary<string, object>
                {
                    ["room_name"] = roomName ?? "",
                    ["password"] = password ?? "",
                    ["max_players"] = maxPlayers > 0 ? maxPlayers : this.maxPlayers,
                }
            };

            NetworkClient.SendJson(msg);
        }

        /// <summary>
        /// 加入房间
        /// </summary>
        public void JoinRoom(string roomId, string password = "")
        {
            if (isInRoom)
            {
                OnRoomError?.Invoke("已在房间中");
                return;
            }

            var msg = new Dictionary<string, object>
            {
                ["type"] = 11,
                ["seq"] = NetworkClient.connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["room_join_req"] = new Dictionary<string, object>
                {
                    ["room_id"] = roomId,
                    ["password"] = password ?? "",
                }
            };

            NetworkClient.SendJson(msg);
        }

        /// <summary>
        /// 离开房间
        /// </summary>
        public void LeaveRoom()
        {
            if (!isInRoom) return;

            var msg = new Dictionary<string, object>
            {
                ["type"] = 12,
                ["seq"] = NetworkClient.connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            NetworkClient.SendJson(msg);
            CurrentRoom = null;
            Players.Clear();
            OnRoomLeft?.Invoke();
        }

        /// <summary>
        /// 获取房间列表
        /// </summary>
        public void GetRoomList(int page = 1, int pageSize = 20)
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = 14,
                ["seq"] = NetworkClient.connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["room_list_req"] = new Dictionary<string, object>
                {
                    ["page"] = page,
                    ["page_size"] = pageSize,
                }
            };

            NetworkClient.SendJson(msg);
        }

        /// <summary>
        /// 设置准备状态
        /// </summary>
        public void SetReady(bool ready)
        {
            if (!isInRoom) return;

            var msg = new Dictionary<string, object>
            {
                ["type"] = 15,
                ["seq"] = NetworkClient.connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["room_ready_req"] = new Dictionary<string, object>
                {
                    ["ready"] = ready,
                }
            };

            NetworkClient.SendJson(msg);
        }

        /// <summary>
        /// 开始游戏（仅房主）
        /// </summary>
        public void StartGame()
        {
            if (!isInRoom || !isHost)
            {
                OnRoomError?.Invoke("只有房主可以开始游戏");
                return;
            }

            var msg = new Dictionary<string, object>
            {
                ["type"] = 16,
                ["seq"] = NetworkClient.connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            NetworkClient.SendJson(msg);
        }

        /// <summary>
        /// 快速匹配
        /// </summary>
        public void QuickMatch()
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = 18,
                ["seq"] = NetworkClient.connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["room_match_req"] = new Dictionary<string, object>
                {
                    ["max_players"] = maxPlayers,
                }
            };

            NetworkClient.SendJson(msg);
        }

        #endregion

        #region Server Message Processing

        /// <summary>
        /// 处理房间状态更新（由NetworkClient调用）
        /// </summary>
        public static void OnRoomStateUpdate(Dictionary<string, object> roomData)
        {
            if (singleton == null) return;

            var state = RoomState.FromDict(roomData);
            singleton.CurrentRoom = state;

            // 更新玩家列表
            singleton.Players.Clear();
            if (roomData.TryGetValue("players", out var playersObj) && playersObj is List<object> playerList)
            {
                foreach (var p in playerList)
                {
                    if (p is Dictionary<string, object> pDict)
                    {
                        singleton.Players.Add(RoomPlayerInfo.FromDict(pDict));
                    }
                }
            }

            singleton.OnRoomUpdated?.Invoke(state);
        }

        #endregion

        #region Virtual Callbacks (子类重写)

        /// <summary>房间创建后</summary>
        public virtual void OnRoomCreatedCallback() { }

        /// <summary>加入房间后</summary>
        public virtual void OnRoomJoinedCallback() { }

        /// <summary>玩家加入房间</summary>
        public virtual void OnPlayerEnteredRoom(RoomPlayerInfo player) { }

        /// <summary>玩家离开房间</summary>
        public virtual void OnPlayerLeftRoom(RoomPlayerInfo player) { }

        /// <summary>所有玩家准备</summary>
        public virtual void OnAllPlayersReady() { }

        /// <summary>游戏开始</summary>
        public virtual void OnGameStartedCallback() { }

        #endregion
    }

    #region Data Structures

    /// <summary>
    /// 房间状态
    /// </summary>
    [Serializable]
    public class RoomState
    {
        public string roomId;
        public string roomName;
        public string state; // WAITING, READY, PLAYING, FINISHED
        public int maxPlayers;
        public string hostId;

        public static RoomState FromDict(Dictionary<string, object> dict)
        {
            return new RoomState
            {
                roomId = dict.TryGetValue("room_id", out var id) ? id as string : "",
                roomName = dict.TryGetValue("room_name", out var name) ? name as string : "",
                state = dict.TryGetValue("state", out var st) ? st as string : "WAITING",
                maxPlayers = dict.TryGetValue("max_players", out var max) ? Convert.ToInt32(max) : 4,
                hostId = dict.TryGetValue("host_id", out var host) ? host as string : "",
            };
        }
    }

    /// <summary>
    /// 房间玩家信息
    /// </summary>
    [Serializable]
    public class RoomPlayerInfo
    {
        public string playerId;
        public string nickname;
        public string avatarUrl;
        public bool isReady;
        public bool isHost;
        public int slotIndex;

        public static RoomPlayerInfo FromDict(Dictionary<string, object> dict)
        {
            return new RoomPlayerInfo
            {
                playerId = dict.TryGetValue("player_id", out var id) ? id as string : "",
                nickname = dict.TryGetValue("nickname", out var nick) ? nick as string : "",
                avatarUrl = dict.TryGetValue("avatar_url", out var url) ? url as string : "",
                isReady = dict.TryGetValue("is_ready", out var ready) && Convert.ToBoolean(ready),
                isHost = dict.TryGetValue("is_host", out var host) && Convert.ToBoolean(host),
                slotIndex = dict.TryGetValue("slot_index", out var slot) ? Convert.ToInt32(slot) : 0,
            };
        }
    }

    #endregion
}
