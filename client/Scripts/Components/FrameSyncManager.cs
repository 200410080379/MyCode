using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 帧同步管理器（客户端）
    /// 实现确定性帧同步：接收服务端确认帧后执行逻辑
    /// 
    /// 核心流程：
    /// 1. 客户端每帧采集输入并发送到服务端
    /// 2. 服务端收集所有玩家输入并广播确认
    /// 3. 客户端收到确认后执行逻辑
    /// 4. 保证所有客户端在相同帧执行相同输入
    /// </summary>
    public class FrameSyncManager : MonoBehaviour
    {
        #region Singleton
        
        public static FrameSyncManager singleton { get; private set; }
        
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
        
        [Header("帧同步配置")]
        [Tooltip("帧率（每秒帧数）")]
        public int frameRate = 20;
        
        [Tooltip("最大预测帧数（超过此数暂停）")]
        public int maxPredictionFrames = 10;
        
        [Tooltip("是否启用客户端预测")]
        public bool enablePrediction = true;
        
        [Tooltip("是否启用回滚")]
        public bool enableRollback = true;
        
        [Tooltip("回滚最大帧数")]
        public int maxRollbackFrames = 30;
        
        #endregion
        
        #region State
        
        /// <summary>当前帧号</summary>
        public long currentFrame { get; private set; }
        
        /// <summary>服务端确认帧号</summary>
        public long confirmedFrame { get; private set; }
        
        /// <summary>是否已同步</summary>
        public bool isSynced { get; private set; }
        
        /// <summary>是否暂停</summary>
        public bool isPaused { get; private set; }
        
        // 帧缓冲区
        private FrameBuffer frameBuffer;
        
        // 待发送输入队列
        private Queue<InputFrame> pendingInputs = new Queue<InputFrame>();
        
        // 本地输入历史（用于回滚）
        private Dictionary<long, byte[]> localInputHistory = new Dictionary<long, byte[]>();
        
        // 帧执行回调
        public Action<long, Dictionary<uint, byte[]>> onFrameExecute;
        
        // 输入采集回调
        public Func<byte[]> onCollectInput;
        
        // 预测执行回调
        public Action<long, byte[]> onPredictFrame;
        
        // 回滚回调
        public Action<long> onRollback;
        
        // 帧确认回调
        public Action<long> onFrameConfirmed;
        
        // 帧间隔
        private float frameInterval;
        private float frameTimer;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Start()
        {
            frameInterval = 1f / frameRate;
            frameBuffer = new FrameBuffer(maxRollbackFrames);
        }
        
        private void Update()
        {
            if (!isSynced || isPaused) return;
            
            frameTimer += Time.deltaTime;
            
            // 按帧率采集输入
            if (frameTimer >= frameInterval)
            {
                frameTimer -= frameInterval;
                CollectAndSendInput();
            }
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// 开始帧同步
        /// </summary>
        public void StartSync(long startFrame = 0)
        {
            currentFrame = startFrame;
            confirmedFrame = startFrame;
            isSynced = true;
            isPaused = false;
            frameTimer = 0f;
            
            Debug.Log($"[FrameSync] 开始同步，起始帧: {startFrame}");
        }
        
        /// <summary>
        /// 停止帧同步
        /// </summary>
        public void StopSync()
        {
            isSynced = false;
            frameBuffer.Clear();
            pendingInputs.Clear();
            localInputHistory.Clear();
            
            Debug.Log("[FrameSync] 停止同步");
        }
        
        /// <summary>
        /// 暂停/恢复
        /// </summary>
        public void SetPaused(bool paused)
        {
            isPaused = paused;
            Debug.Log($"[FrameSync] {(paused ? "暂停" : "恢复")}");
        }
        
        /// <summary>
        /// 处理服务端确认帧
        /// </summary>
        public void OnFrameConfirm(long frame, Dictionary<uint, byte[]> inputs)
        {
            if (!isSynced) return;
            
            // 检测是否有丢帧/乱序
            if (frame <= confirmedFrame)
            {
                // 旧帧，可能是重传，忽略
                return;
            }
            
            // 检测是否需要回滚
            if (enableRollback && frame < currentFrame)
            {
                // 需要回滚到确认帧重新执行
                RollbackTo(frame);
            }
            
            // 存储确认的输入
            frameBuffer.StoreFrame(frame, inputs);
            
            // 更新确认帧号
            confirmedFrame = frame;
            
            // 执行帧逻辑
            ExecuteFrame(frame, inputs);
            
            // 回调
            onFrameConfirmed?.Invoke(frame);
        }
        
        /// <summary>
        /// 获取帧历史（用于调试）
        /// </summary>
        public FrameBuffer GetFrameBuffer()
        {
            return frameBuffer;
        }
        
        #endregion
        
        #region Internal Methods
        
        /// <summary>
        /// 采集并发送输入
        /// </summary>
        private void CollectAndSendInput()
        {
            // 采集本地输入
            byte[] input = onCollectInput?.Invoke();
            if (input == null) input = new byte[0];
            
            // 存储本地输入历史
            localInputHistory[currentFrame] = input;
            
            // 发送到服务端
            SendInputFrame(currentFrame, input);
            
            // 客户端预测
            if (enablePrediction)
            {
                // 预测执行当前帧
                onPredictFrame?.Invoke(currentFrame, input);
            }
            
            // 推进本地帧号
            currentFrame++;
            
            // 检查预测是否超过阈值
            if (currentFrame - confirmedFrame > maxPredictionFrames)
            {
                Debug.LogWarning($"[FrameSync] 预测帧数超过阈值，暂停: {currentFrame - confirmedFrame}");
                isPaused = true;
            }
        }
        
        /// <summary>
        /// 发送输入帧到服务端
        /// </summary>
        private void SendInputFrame(long frame, byte[] input)
        {
            if (!NetworkClient.isConnected) return;
            
            var msg = new Dictionary<string, object>
            {
                ["type"] = 72, // MSG_TYPE_INPUT_FRAME
                ["seq"] = NetworkClient.connection?.nextSeq() ?? 0,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["frame"] = frame,
                ["input_data"] = Convert.ToBase64String(input),
                ["input_hash"] = ComputeInputHash(input),
            };
            
            NetworkClient.SendJson(msg);
        }
        
        /// <summary>
        /// 执行帧逻辑
        /// </summary>
        private void ExecuteFrame(long frame, Dictionary<uint, byte[]> inputs)
        {
            // 调用游戏逻辑执行帧
            onFrameExecute?.Invoke(frame, inputs);
        }
        
        /// <summary>
        /// 回滚到指定帧
        /// </summary>
        private void RollbackTo(long targetFrame)
        {
            if (!enableRollback) return;
            
            Debug.Log($"[FrameSync] 回滚到帧 {targetFrame}");
            
            // 回调通知游戏逻辑回滚
            onRollback?.Invoke(targetFrame);
            
            // 重新执行帧
            for (long f = targetFrame; f <= currentFrame; f++)
            {
                var inputs = frameBuffer.GetFrameInputs(f);
                if (inputs != null)
                {
                    ExecuteFrame(f, inputs);
                }
            }
        }
        
        /// <summary>
        /// 计算输入哈希（用于验证）
        /// </summary>
        private string ComputeInputHash(byte[] input)
        {
            if (input == null || input.Length == 0) return "";
            
            // 简化实现：使用前4字节
            int hash = 0;
            for (int i = 0; i < Math.Min(input.Length, 8); i++)
            {
                hash = hash * 31 + input[i];
            }
            return hash.ToString("x8");
        }
        
        #endregion
    }
    
    /// <summary>
    /// 输入帧数据
    /// </summary>
    public struct InputFrame
    {
        public long frame;
        public byte[] inputData;
        public long timestamp;
    }
}
