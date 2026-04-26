using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 房间玩家组件
    /// 参考 Mirror NetworkRoomPlayer 设计
    /// 挂载到房间内的玩家预制体上
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    [AddComponentMenu("MiniLink/NetworkRoomPlayer")]
    public class NetworkRoomPlayer : NetworkBehaviour
    {
        #region Serialized Fields

        [Header("Player Info")]
        [SerializeField] private string playerName = "";
        [SerializeField] private string avatarUrl = "";
        [SerializeField] private int slotIndex = 0;

        #endregion

        #region SyncVars (会生成脏标记)

        // 注意：这里使用手动实现的SyncVar，实际项目中应使用代码生成器
        private bool ready;

        public bool IsReady
        {
            get => ready;
            set
            {
                if (ready == value) return;
                // bool oldReady = ready;
                ready = value;
                SetDirtyBit(0);
                // OnReadyChanged?.Invoke(oldReady, ready);
            }
        }

        #endregion

        #region Properties

        /// <summary>是否是本地玩家</summary>
        public bool IsLocalPlayer => netIdentity?.isLocalPlayer ?? false;

        /// <summary>是否是房主</summary>
        public bool IsHost { get; internal set; }

        /// <summary>玩家ID</summary>
        public string PlayerId => netIdentity?.ownerConnId;

        /// <summary>房间管理器引用</summary>
        public NetworkRoomManager RoomManager => NetworkRoomManager.singleton;

        #endregion

        #region Unity Lifecycle

        public override void OnStartClient()
        {
            // 注册到房间管理器
            if (RoomManager != null)
            {
                Debug.Log($"[RoomPlayer] 已加入房间: {PlayerId}");
            }
        }

        public override void OnStopClient()
        {
            if (RoomManager != null)
            {
                Debug.Log($"[RoomPlayer] 离开房间: {PlayerId}");
            }
        }

        #endregion

        #region Room Actions

        /// <summary>
        /// 切换准备状态
        /// </summary>
        public void ToggleReady()
        {
            if (!IsLocalPlayer) return;
            SetReady(!ready);
        }

        /// <summary>
        /// 设置准备状态
        /// </summary>
        public void SetReady(bool value)
        {
            if (!IsLocalPlayer) return;

            ready = value;
            SetDirtyBit(0);

            if (RoomManager != null)
            {
                RoomManager.SetReady(value);
            }
            else
            {
                Debug.LogWarning("[RoomPlayer] RoomManager 不存在，无法同步准备状态");
            }
        }

        #endregion

        #region SyncVar Serialization (手动实现)

        internal override void SerializeSyncVars(NetworkWriter writer, bool initialState)
        {
            if (initialState || IsDirty(0))
            {
                writer.WriteBool(ready);
            }

            base.SerializeSyncVars(writer, initialState);
        }

        internal override void DeserializeSyncVars(NetworkReader reader, bool initialState)
        {
            if (initialState || IsDirty(0))
            {
                bool oldReady = ready;
                ready = reader.ReadBool();

                if (!initialState && oldReady != ready)
                {
                    // OnReadyChanged?.Invoke(oldReady, ready);
                    Debug.Log($"[RoomPlayer] 准备状态变更: {ready}");
                }
            }

            base.DeserializeSyncVars(reader, initialState);
        }

        #endregion
    }
}
