using System;
using System.Collections.Generic;

namespace MiniLink
{
    /// <summary>
    /// 帧缓冲区
    /// 存储帧历史用于回滚和断线重连
    /// </summary>
    public class FrameBuffer
    {
        private int capacity;
        private Dictionary<long, Dictionary<uint, byte[]>> buffer;
        private long oldestFrame;
        private long newestFrame;
        
        public FrameBuffer(int capacity = 60)
        {
            this.capacity = capacity;
            this.buffer = new Dictionary<long, Dictionary<uint, byte[]>>();
            this.oldestFrame = -1;
            this.newestFrame = -1;
        }
        
        /// <summary>
        /// 存储帧数据
        /// </summary>
        public void StoreFrame(long frame, Dictionary<uint, byte[]> inputs)
        {
            buffer[frame] = inputs;
            
            if (oldestFrame < 0) oldestFrame = frame;
            newestFrame = frame;
            
            // 清理过期帧
            while (buffer.Count > capacity && oldestFrame < frame)
            {
                buffer.Remove(oldestFrame);
                oldestFrame++;
            }
        }
        
        /// <summary>
        /// 获取帧输入
        /// </summary>
        public Dictionary<uint, byte[]> GetFrameInputs(long frame)
        {
            if (buffer.TryGetValue(frame, out var inputs))
            {
                return inputs;
            }
            return null;
        }
        
        /// <summary>
        /// 获取指定玩家在指定帧的输入
        /// </summary>
        public byte[] GetPlayerInput(long frame, uint netId)
        {
            var inputs = GetFrameInputs(frame);
            if (inputs != null && inputs.TryGetValue(netId, out var input))
            {
                return input;
            }
            return null;
        }
        
        /// <summary>
        /// 检查帧是否存在
        /// </summary>
        public bool HasFrame(long frame)
        {
            return buffer.ContainsKey(frame);
        }
        
        /// <summary>
        /// 获取帧范围
        /// </summary>
        public (long oldest, long newest) GetFrameRange()
        {
            return (oldestFrame, newestFrame);
        }
        
        /// <summary>
        /// 清空缓冲区
        /// </summary>
        public void Clear()
        {
            buffer.Clear();
            oldestFrame = -1;
            newestFrame = -1;
        }
        
        /// <summary>
        /// 获取存储的帧数
        /// </summary>
        public int Count => buffer.Count;
    }
}
