using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 网络延迟补偿管理器
    /// 
    /// 核心功能：
    /// 1. 计算网络往返延迟(RTT/Ping)
    /// 2. 计算时钟偏差（客户端与服务端时间差）
    /// 3. 预测服务端当前时间
    /// 4. 延迟补偿：对历史状态进行命中判定
    /// 
    /// 参考：Valve Source引擎、Overwatch的拉格补偿
    /// </summary>
    public class LagCompensationManager : MonoBehaviour
    {
        #region Singleton
        
        public static LagCompensationManager singleton { get; private set; }
        
        private void Awake()
        {
            if (singleton != null && singleton != this)
            {
                Destroy(gameObject);
                return;
            }
            singleton = this;
        }
        
        #endregion
        
        #region Config
        
        [Header("延迟补偿配置")]
        [Tooltip("历史状态最大缓存秒数")]
        public float maxHistorySeconds = 1f;
        
        [Tooltip("最大补偿秒数（防止作弊）")]
        public float maxCompensationSeconds = 0.5f;
        
        [Tooltip("RTT采样数量")]
        public int rttSampleCount = 10;
        
        [Tooltip("是否启用延迟补偿")]
        public bool enableCompensation = true;
        
        #endregion
        
        #region State
        
        /// <summary>当前RTT（毫秒）</summary>
        public float currentRtt { get; private set; }
        
        /// <summary>平均RTT（毫秒）</summary>
        public float averageRtt { get; private set; }
        
        /// <summary>最小RTT（毫秒）</summary>
        public float minRtt { get; private set; } = float.MaxValue;
        
        /// <summary>时钟偏差（毫秒，服务端时间 - 本地时间）</summary>
        public float clockOffset { get; private set; }
        
        /// <summary>单向延迟估算（毫秒）</summary>
        public float oneWayDelay => averageRtt / 2f;
        
        // RTT采样队列
        private Queue<float> rttSamples = new Queue<float>();
        
        // 时间戳历史（用于计算时钟偏差）
        private List<TimestampSample> timestampSamples = new List<TimestampSample>();
        
        // 对象状态历史
        // netId -> List<StateSnapshot>
        private Dictionary<uint, List<StateSnapshot>> stateHistory = new Dictionary<uint, List<StateSnapshot>>();
        
        // 补偿中的对象（回滚用）
        private Dictionary<uint, StateSnapshot> originalStates = new Dictionary<uint, StateSnapshot>();
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// 记录RTT样本（收到心跳响应时调用）
        /// </summary>
        public void RecordRttSample(float rtt)
        {
            currentRtt = rtt;
            
            rttSamples.Enqueue(rtt);
            if (rttSamples.Count > rttSampleCount)
            {
                rttSamples.Dequeue();
            }
            
            // 计算平均值
            float sum = 0f;
            int count = 0;
            foreach (var sample in rttSamples)
            {
                sum += sample;
                count++;
            }
            
            if (count > 0)
            {
                averageRtt = sum / count;
            }
            
            // 记录最小RTT
            if (rtt < minRtt)
            {
                minRtt = rtt;
            }
        }
        
        /// <summary>
        /// 计算时钟偏差
        /// 使用NTP风格的时钟同步算法
        /// </summary>
        public void RecordTimestampSample(long clientSendTime, long serverReceiveTime, long serverSendTime, long clientReceiveTime)
        {
            // NTP公式：offset = ((t2 - t1) + (t3 - t4)) / 2
            // t1: clientSendTime
            // t2: serverReceiveTime
            // t3: serverSendTime
            // t4: clientReceiveTime
            
            float offset = ((serverReceiveTime - clientSendTime) + (serverSendTime - clientReceiveTime)) / 2f;
            
            timestampSamples.Add(new TimestampSample
            {
                offset = offset,
                timestamp = clientReceiveTime,
            });
            
            // 保留最近10个样本
            if (timestampSamples.Count > 10)
            {
                timestampSamples.RemoveAt(0);
            }
            
            // 计算中位数（更稳定）
            CalculateClockOffset();
        }
        
        /// <summary>
        /// 获取修正后的服务端时间
        /// </summary>
        public long GetServerTime()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)clockOffset;
        }
        
        /// <summary>
        /// 获取补偿时间点（用于命中判定）
        /// </summary>
        public long GetCompensationTime()
        {
            long serverTime = GetServerTime();
            long compensation = Math.Min((long)(oneWayDelay + maxCompensationSeconds * 1000), (long)maxCompensationSeconds * 1000);
            return serverTime - compensation;
        }
        
        /// <summary>
        /// 记录对象状态快照
        /// </summary>
        public void RecordState(uint netId, Vector3 position, Quaternion rotation, byte[] customData = null)
        {
            if (!stateHistory.TryGetValue(netId, out var history))
            {
                history = new List<StateSnapshot>();
                stateHistory[netId] = history;
            }
            
            history.Add(new StateSnapshot
            {
                timestamp = GetServerTime(),
                position = position,
                rotation = rotation,
                customData = customData,
            });
            
            // 清理过期快照
            RemoveOldSnapshots(history);
        }
        
        /// <summary>
        /// 开始延迟补偿（回滚到指定时间）
        /// </summary>
        public void BeginCompensation(long targetTime, params uint[] netIds)
        {
            if (!enableCompensation) return;
            
            originalStates.Clear();
            
            foreach (var netId in netIds)
            {
                if (!stateHistory.TryGetValue(netId, out var history)) continue;
                
                // 找到最接近目标时间的状态
                var snapshot = FindClosestSnapshot(history, targetTime);
                if (snapshot == null) continue;
                
                // 保存当前状态
                var current = GetCurrentState(netId);
                if (current != null)
                {
                    originalStates[netId] = current;
                }
                
                // 回滚到历史状态
                RestoreState(netId, snapshot);
            }
            
            Debug.Log($"[LagCompensation] 开始补偿，目标时间: {targetTime}, 对象数: {netIds.Length}");
        }
        
        /// <summary>
        /// 结束延迟补偿（恢复原始状态）
        /// </summary>
        public void EndCompensation()
        {
            if (!enableCompensation) return;
            
            foreach (var kvp in originalStates)
            {
                RestoreState(kvp.Key, kvp.Value);
            }
            
            originalStates.Clear();
        }
        
        /// <summary>
        /// 命中判定（带延迟补偿）
        /// </summary>
        public bool CheckHit(uint targetNetId, Vector3 origin, Vector3 direction, float maxDistance)
        {
            // 获取补偿时间点
            long compensationTime = GetCompensationTime();
            
            // 回滚目标对象
            BeginCompensation(compensationTime, targetNetId);
            
            try
            {
                // 执行射线检测
                RaycastHit hit;
                if (Physics.Raycast(origin, direction, out hit, maxDistance))
                {
                    // 检查是否命中目标
                    var identity = hit.collider.GetComponent<NetworkIdentity>();
                    if (identity != null && identity.netId == targetNetId)
                    {
                        return true;
                    }
                }
                
                return false;
            }
            finally
            {
                // 恢复状态
                EndCompensation();
            }
        }
        
        /// <summary>
        /// 获取网络状态报告
        /// </summary>
        public NetworkStats GetStats()
        {
            return new NetworkStats
            {
                currentRtt = currentRtt,
                averageRtt = averageRtt,
                minRtt = minRtt,
                oneWayDelay = oneWayDelay,
                clockOffset = clockOffset,
                compensationTime = GetCompensationTime(),
                serverTime = GetServerTime(),
                objectCount = stateHistory.Count,
            };
        }
        
        #endregion
        
        #region Internal Methods
        
        /// <summary>
        /// 计算时钟偏差（中位数滤波）
        /// </summary>
        private void CalculateClockOffset()
        {
            if (timestampSamples.Count == 0) return;
            
            // 排序并取中位数
            var sorted = new List<float>();
            foreach (var sample in timestampSamples)
            {
                sorted.Add(sample.offset);
            }
            sorted.Sort();
            
            int mid = sorted.Count / 2;
            clockOffset = sorted.Count % 2 == 0
                ? (sorted[mid - 1] + sorted[mid]) / 2f
                : sorted[mid];
        }
        
        /// <summary>
        /// 移除过期的快照
        /// </summary>
        private void RemoveOldSnapshots(List<StateSnapshot> history)
        {
            long cutoffTime = GetServerTime() - (long)(maxHistorySeconds * 1000);
            
            while (history.Count > 0 && history[0].timestamp < cutoffTime)
            {
                history.RemoveAt(0);
            }
        }
        
        /// <summary>
        /// 查找最接近目标时间的快照
        /// </summary>
        private StateSnapshot FindClosestSnapshot(List<StateSnapshot> history, long targetTime)
        {
            StateSnapshot closest = null;
            long minDiff = long.MaxValue;
            
            foreach (var snapshot in history)
            {
                long diff = Math.Abs(snapshot.timestamp - targetTime);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closest = snapshot;
                }
            }
            
            return closest;
        }
        
        /// <summary>
        /// 获取当前状态
        /// </summary>
        private StateSnapshot GetCurrentState(uint netId)
        {
            var obj = UnityEngine.Object.FindObjectsOfType<NetworkIdentity>();
            foreach (var identity in obj)
            {
                if (identity.netId == netId)
                {
                    return new StateSnapshot
                    {
                        timestamp = GetServerTime(),
                        position = identity.transform.position,
                        rotation = identity.transform.rotation,
                    };
                }
            }
            return null;
        }
        
        /// <summary>
        /// 恢复状态
        /// </summary>
        private void RestoreState(uint netId, StateSnapshot snapshot)
        {
            var obj = UnityEngine.Object.FindObjectsOfType<NetworkIdentity>();
            foreach (var identity in obj)
            {
                if (identity.netId == netId)
                {
                    identity.transform.position = snapshot.position;
                    identity.transform.rotation = snapshot.rotation;
                    break;
                }
            }
        }
        
        #endregion
        
        #region Debug
        
        /// <summary>
        /// 绘制调试Gizmos
        /// </summary>
        private void OnDrawGizmos()
        {
            if (!enableCompensation) return;
            
            // 绘制状态历史轨迹
            foreach (var kvp in stateHistory)
            {
                var history = kvp.Value;
                if (history.Count < 2) continue;
                
                Gizmos.color = Color.yellow;
                for (int i = 1; i < history.Count; i++)
                {
                    Gizmos.DrawLine(history[i - 1].position, history[i].position);
                }
            }
        }
        
        #endregion
    }
    
    /// <summary>
    /// 时间戳样本
    /// </summary>
    public struct TimestampSample
    {
        public float offset;
        public long timestamp;
    }
    
    /// <summary>
    /// 状态快照
    /// </summary>
    public class StateSnapshot
    {
        public long timestamp;
        public Vector3 position;
        public Quaternion rotation;
        public byte[] customData;
    }
    
    /// <summary>
    /// 网络统计信息
    /// </summary>
    public class NetworkStats
    {
        public float currentRtt;
        public float averageRtt;
        public float minRtt;
        public float oneWayDelay;
        public float clockOffset;
        public long compensationTime;
        public long serverTime;
        public int objectCount;
        
        public override string ToString()
        {
            return $"RTT: {averageRtt:F1}ms (min: {minRtt:F1}), Delay: {oneWayDelay:F1}ms, Offset: {clockOffset:F1}ms";
        }
    }
}
