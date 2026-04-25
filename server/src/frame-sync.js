/**
 * MiniLink FrameSyncManager - 帧同步管理器（服务端）
 * 实现确定性帧同步：所有客户端在相同帧执行相同输入
 * 
 * 核心原理：
 * 1. 服务端收集所有玩家同一帧的输入
 * 2. 等待所有玩家提交或超时
 * 3. 广播确认帧给所有玩家
 * 4. 客户端收到确认后执行逻辑
 */
class FrameSyncManager {
  constructor(server) {
    this.server = server;
    
    // 帧同步配置
    this.frameRate = 20; // 每秒20帧
    this.frameInterval = 1000 / this.frameRate; // 50ms
    this.inputTimeout = 100; // 输入超时100ms
    
    // 房间帧状态
    // roomId -> FrameState
    this.roomFrameStates = new Map();
    
    // 玩家最后输入帧
    // connId -> lastFrame
    this.playerLastFrame = new Map();
    
    // 启动帧同步定时器
    this._startFrameLoop();
  }
  
  /**
   * 初始化房间的帧同步
   */
  initRoom(roomId, playerCount) {
    this.roomFrameStates.set(roomId, {
      currentFrame: 0,
      expectedPlayers: playerCount,
      pendingInputs: new Map(), // connId -> input
      frameHistory: [], // 最近帧历史（用于断线重连）
      lastFrameTime: Date.now(),
      paused: false,
    });
  }
  
  /**
   * 移除房间的帧同步
   */
  removeRoom(roomId) {
    this.roomFrameStates.delete(roomId);
  }
  
  /**
   * 玩家加入房间
   */
  playerJoin(roomId, connId) {
    const state = this.roomFrameStates.get(roomId);
    if (!state) return;
    
    state.expectedPlayers++;
    this.playerLastFrame.set(connId, state.currentFrame);
  }
  
  /**
   * 玩家离开房间
   */
  playerLeave(roomId, connId) {
    const state = this.roomFrameStates.get(roomId);
    if (!state) return;
    
    state.expectedPlayers--;
    state.pendingInputs.delete(connId);
    this.playerLastFrame.delete(connId);
  }
  
  /**
   * 接收玩家输入
   */
  receiveInput(connId, msg) {
    const conn = this.server.connections.get(connId);
    if (!conn || !conn.roomId) return;
    
    const state = this.roomFrameStates.get(conn.roomId);
    if (!state || state.paused) return;
    
    const { frame, input_data, input_hash } = msg;
    
    // 验证帧号（防止客户端作弊）
    if (frame < state.currentFrame - 10) {
      // 客户端落后太多，可能需要快进
      console.warn(`[FrameSync] 玩家 ${connId} 帧号落后: ${frame} vs ${state.currentFrame}`);
      return;
    }
    
    // 存储输入
    state.pendingInputs.set(connId, {
      frame,
      inputData: input_data,
      inputHash: input_hash,
      receivedAt: Date.now(),
    });
    
    this.playerLastFrame.set(connId, frame);
    
    // 检查是否所有玩家都已提交
    this._tryAdvanceFrame(conn.roomId);
  }
  
  /**
   * 暂停/恢复帧同步
   */
  setPaused(roomId, paused) {
    const state = this.roomFrameStates.get(roomId);
    if (!state) return;
    
    state.paused = paused;
    
    // 广播暂停/恢复消息
    this.server.broadcastToRoom(roomId, {
      type: 70, // MSG_TYPE_FRAME_PAUSE
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      frame_pause_msg: {
        paused,
        frame: state.currentFrame,
      },
    });
  }
  
  /**
   * 获取帧历史（用于断线重连）
   */
  getFrameHistory(roomId, fromFrame) {
    const state = this.roomFrameStates.get(roomId);
    if (!state) return [];
    
    return state.frameHistory.filter(f => f.frame >= fromFrame);
  }
  
  // ==================== 内部方法 ====================
  
  /**
   * 启动帧同步循环
   */
  _startFrameLoop() {
    this.frameIntervalId = setInterval(() => {
      this._tick();
    }, this.frameInterval);
  }
  
  /**
   * 帧同步主循环
   */
  _tick() {
    const now = Date.now();
    
    for (const [roomId, state] of this.roomFrameStates) {
      if (state.paused) continue;
      
      // 检查是否超时
      const elapsed = now - state.lastFrameTime;
      if (elapsed >= this.inputTimeout) {
        // 超时，强制推进帧
        this._forceAdvanceFrame(roomId, state);
      } else {
        // 正常检查
        this._tryAdvanceFrame(roomId);
      }
    }
  }
  
  /**
   * 尝试推进帧
   */
  _tryAdvanceFrame(roomId) {
    const state = this.roomFrameStates.get(roomId);
    if (!state || state.paused) return;
    
    // 检查是否所有玩家都已提交输入
    if (state.pendingInputs.size >= state.expectedPlayers) {
      this._advanceFrame(roomId, state);
    }
  }
  
  /**
   * 推进帧（正常）
   */
  _advanceFrame(roomId, state) {
    const now = Date.now();
    
    // 构造确认帧消息
    const confirmMsg = {
      type: 71, // MSG_TYPE_FRAME_CONFIRM
      seq: this.server.nextSeq(),
      timestamp: now,
      frame_confirm_msg: {
        frame: state.currentFrame,
        inputs: this._serializeInputs(state.pendingInputs),
        server_time: now,
      },
    };
    
    // 保存到帧历史
    state.frameHistory.push({
      frame: state.currentFrame,
      inputs: Array.from(state.pendingInputs.entries()),
      time: now,
    });
    
    // 只保留最近60帧历史（3秒）
    if (state.frameHistory.length > 60) {
      state.frameHistory.shift();
    }
    
    // 广播确认帧
    this.server.broadcastToRoom(roomId, confirmMsg);
    
    // 更新状态
    state.currentFrame++;
    state.pendingInputs.clear();
    state.lastFrameTime = now;
  }
  
  /**
   * 强制推进帧（超时）
   */
  _forceAdvanceFrame(roomId, state) {
    const now = Date.now();
    
    // 获取房间内所有玩家
    const room = this.server.roomManager.getRoom(roomId);
    if (!room) return;
    
    // 为未提交输入的玩家生成空输入
    for (const [connId, player] of room.players) {
      if (player.disconnected) continue;
      
      if (!state.pendingInputs.has(connId)) {
        // 使用上一帧的输入或空输入
        const lastInput = this._getLastInput(connId) || { inputData: Buffer.alloc(0) };
        state.pendingInputs.set(connId, {
          frame: state.currentFrame,
          inputData: lastInput.inputData,
          receivedAt: now,
          timeout: true,
        });
      }
    }
    
    // 推进帧
    this._advanceFrame(roomId, state);
    
    // 记录超时统计
    this._recordTimeout(roomId);
  }
  
  /**
   * 序列化输入
   */
  _serializeInputs(pendingInputs) {
    const result = [];
    for (const [connId, input] of pendingInputs) {
      const conn = this.server.connections.get(connId);
      result.push({
        net_id: conn?.playerNetId || 0,
        frame: input.frame,
        input_data: input.inputData,
        timeout: input.timeout || false,
      });
    }
    return result;
  }
  
  /**
   * 获取玩家最后输入
   */
  _getLastInput(connId) {
    const lastFrame = this.playerLastFrame.get(connId);
    // 从帧历史中查找
    return null; // 简化实现
  }
  
  /**
   * 记录超时统计
   */
  _recordTimeout(roomId) {
    // 用于监控和调试
  }
  
  /**
   * 获取统计信息
   */
  getStats() {
    const stats = {
      rooms: this.roomFrameStates.size,
      details: {},
    };
    
    for (const [roomId, state] of this.roomFrameStates) {
      stats.details[roomId] = {
        currentFrame: state.currentFrame,
        expectedPlayers: state.expectedPlayers,
        pendingCount: state.pendingInputs.size,
        historySize: state.frameHistory.length,
        paused: state.paused,
      };
    }
    
    return stats;
  }
  
  /**
   * 清理
   */
  destroy() {
    if (this.frameIntervalId) {
      clearInterval(this.frameIntervalId);
    }
  }
}

module.exports = { FrameSyncManager };
