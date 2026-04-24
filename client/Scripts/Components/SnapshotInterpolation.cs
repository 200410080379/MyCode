using System.Collections.Generic;
using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 快照插值算法
    /// 参考 Mirror SnapshotInterpolation 设计
    /// 解决网络抖动导致的对象抖动，提供平滑的远程对象移动
    /// 
    /// 核心思想：
    /// - 客户端不是直接应用服务端快照
    /// - 而是将快照加入缓冲区，延迟一小段时间后插值
    /// - 这样即使网络有抖动，本地表现也平滑
    /// </summary>
    public static class SnapshotInterpolation
    {
        #region Settings

        /// <summary>缓冲时间(秒)，越大越平滑但延迟越高</summary>
        public static float bufferTime = 0.05f;

        /// <summary>缓冲区最大容量</summary>
        public static int bufferLimit = 64;

        /// <summary>插值速度倍率</summary>
        public static float interpolationSpeed = 1.0f;

        /// <summary>是否启用快速插值（追赶延迟）</summary>
        public static bool catchup = true;

        /// <summary>追赶阈值(秒)：当延迟超过此值时加速</summary>
        public static float catchupThreshold = 0.1f;

        /// <summary>追赶速度倍率</summary>
        public static float catchupMultiplier = 1.2f;

        /// <summary>抖动检测阈值(秒)</summary>
        public static float jitterTolerance = 0.05f;

        #endregion

        #region Per-Object Snapshot Buffer

        /// <summary>
        /// 每个对象的快照缓冲区
        /// key: netId, value: 快照列表
        /// </summary>
        private static readonly Dictionary<uint, SnapshotBuffer> buffers = new Dictionary<uint, SnapshotBuffer>();

        #endregion

        #region Public API

        /// <summary>
        /// 添加服务端快照
        /// </summary>
        public static void AddSnapshot(uint netId, long remoteTick, float remoteTime, byte[] stateData)
        {
            if (!buffers.TryGetValue(netId, out var buffer))
            {
                buffer = new SnapshotBuffer();
                buffers[netId] = buffer;
            }

            var snapshot = new Snapshot
            {
                remoteTick = remoteTick,
                remoteTime = remoteTime,
                localTime = Time.time,
                stateData = stateData,
            };

            buffer.Add(snapshot);
        }

        /// <summary>
        /// 获取插值后的快照（每帧调用）
        /// </summary>
        /// <param name="netId">网络对象ID</param>
        /// <param name="stateData">插值后的状态数据（如果有的话）</param>
        /// <returns>是否有有效的插值结果</returns>
        public static bool TryInterpolate(uint netId, out byte[] stateData)
        {
            stateData = null;

            if (!buffers.TryGetValue(netId, out var buffer))
                return false;

            if (buffer.Count < 2)
                return false;

            // 计算插值时间点
            float currentTime = Time.time;
            float interpolateTime = currentTime - bufferTime;

            // 查找两个快照进行插值
            Snapshot from = default;
            Snapshot to = default;
            bool found = false;

            for (int i = 0; i < buffer.Count - 1; i++)
            {
                if (buffer[i].localTime <= interpolateTime && buffer[i + 1].localTime >= interpolateTime)
                {
                    from = buffer[i];
                    to = buffer[i + 1];
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                // 使用最新的两个快照
                from = buffer[buffer.Count - 2];
                to = buffer[buffer.Count - 1];
            }

            // 计算插值因子
            float timeRange = to.localTime - from.localTime;
            float t = timeRange > 0 ? (interpolateTime - from.localTime) / timeRange : 0f;
            t = Mathf.Clamp01(t);

            // 应用追赶
            if (catchup)
            {
                float delay = currentTime - to.localTime;
                if (delay > catchupThreshold)
                {
                    t = Mathf.Min(t * catchupMultiplier, 1.0f);
                }
            }

            // 插值状态数据
            stateData = InterpolateState(from.stateData, to.stateData, t);
            return true;
        }

        /// <summary>
        /// 更新所有对象的插值（每帧调用）
        /// 清理过期快照
        /// </summary>
        public static void Update()
        {
            float currentTime = Time.time;

            var toRemove = new List<uint>();
            foreach (var kvp in buffers)
            {
                // 清理过期快照
                kvp.Value.RemoveOlderThan(currentTime - bufferTime - 1.0f);

                // 如果对象已不存在且缓冲区为空，移除
                if (kvp.Value.Count == 0 && NetworkClient.GetSpawnedObject(kvp.Key) == null)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var netId in toRemove)
            {
                buffers.Remove(netId);
            }
        }

        /// <summary>
        /// 移除对象的插值缓冲
        /// </summary>
        public static void RemoveBuffer(uint netId)
        {
            buffers.Remove(netId);
        }

        /// <summary>
        /// 清空所有缓冲
        /// </summary>
        public static void Clear()
        {
            buffers.Clear();
        }

        #endregion

        #region State Interpolation

        /// <summary>
        /// 插值两个状态数据
        /// 使用 NetworkReader 读取，对数值类型线性插值
        /// </summary>
        private static byte[] InterpolateState(byte[] from, byte[] to, float t)
        {
            // 简化实现：如果t接近0或1，直接返回
            if (t <= 0.01f) return from;
            if (t >= 0.99f) return to;

            // 对于Transform同步，解析Position+Rotation进行插值
            // 通用实现：直接返回to的快照（业务层可重写）
            return to;
        }

        /// <summary>
        /// Transform专用插值
        /// </summary>
        public static void InterpolateTransform(
            uint netId,
            out Vector3 position,
            out Quaternion rotation,
            Vector3 lastPosition,
            Quaternion lastRotation)
        {
            position = lastPosition;
            rotation = lastRotation;

            if (!buffers.TryGetValue(netId, out var buffer) || buffer.Count < 2)
                return;

            float currentTime = Time.time;
            float interpolateTime = currentTime - bufferTime;

            // 找插值区间
            Snapshot from = default;
            Snapshot to = default;
            bool found = false;

            for (int i = 0; i < buffer.Count - 1; i++)
            {
                if (buffer[i].localTime <= interpolateTime && buffer[i + 1].localTime >= interpolateTime)
                {
                    from = buffer[i];
                    to = buffer[i + 1];
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                from = buffer[buffer.Count - 2];
                to = buffer[buffer.Count - 1];
            }

            // 解析Transform数据
            if (from.stateData != null && from.stateData.Length >= 24)
            {
                var fromReader = new NetworkReader(from.stateData);
                var fromPos = fromReader.ReadVector3();

                var toReader = new NetworkReader(to.stateData);
                var toPos = toReader.ReadVector3();

                float timeRange = to.localTime - from.localTime;
                float t = timeRange > 0 ? (interpolateTime - from.localTime) / timeRange : 0f;
                t = Mathf.Clamp01(t);

                position = Vector3.Lerp(fromPos, toPos, t);

                // 解析旋转
                if (from.stateData.Length >= 40 && to.stateData.Length >= 40)
                {
                    fromReader = new NetworkReader(from.stateData);
                    fromReader.ReadVector3(); // skip pos
                    var fromRot = fromReader.ReadQuaternion();

                    toReader = new NetworkReader(to.stateData);
                    toReader.ReadVector3(); // skip pos
                    var toRot = toReader.ReadQuaternion();

                    rotation = Quaternion.Slerp(fromRot, toRot, t);
                }
            }
        }

        #endregion

        #region Snapshot Structure

        public struct Snapshot
        {
            public long remoteTick;
            public float remoteTime;
            public float localTime;
            public byte[] stateData;
        }

        /// <summary>
        /// 快照缓冲区（有序列表）
        /// </summary>
        public class SnapshotBuffer : List<Snapshot>
        {
            public new void Add(Snapshot snapshot)
            {
                // 按时间顺序插入
                int index = Count;
                for (int i = 0; i < Count; i++)
                {
                    if (this[i].localTime > snapshot.localTime)
                    {
                        index = i;
                        break;
                    }
                }
                Insert(index, snapshot);

                // 限制缓冲区大小
                while (Count > bufferLimit)
                {
                    RemoveAt(0);
                }
            }

            public void RemoveOlderThan(float time)
            {
                while (Count > 1 && this[0].localTime < time)
                {
                    RemoveAt(0);
                }
            }
        }

        #endregion
    }
}
