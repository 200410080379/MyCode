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
    // connId -> { frame, inputData }
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
    this.playerLastFrame.set(connId, { frame: state.currentFrame, inputData: null });
  }
  
  /**
   * 玩家离开房间
   */
  playerLeave(roomId, connId) {
    const state = this.roomFrameStates.get(roomId);
    if (!state) return;
    
    state.expectedPlayers = Math.max(1, state.expectedPlayers - 1);
    state.pendingInputs.delete(connId);
    this.playerLastFrame.delete(connId);
  }
  
  /**
   * #12: 接收玩家输入 - 统一接口
   * @param {string} connId - 连接ID
   * @param {string} roomId - 房间ID
   * @param {object} frameInput - { frame, input_data, input_hash }
   */
  receiveInput(connId, roomId, frameInput) {
    const state = this.roomFrameStates.get(roomId);
    if (!state || state.paused) return;
    
    const { frame, input_data, input_hash } = frameInput;
    
    // 验证帧号（防止客户端作弊）
    if (frame < state.currentFrame - 10) {
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
    
    this.playerLastFrame.set(connId, { frame, inputData: input_data });
    
    // 检查是否所有玩家都已提交
    this._tryAdvanceFrame(roomId);
  }
  
  /**
   * 暂停/恢复帧同步
   */
  setPaused(roomId, paused) {
    const state = this.roomFrameStates.get(roomId);
    if (!state) return;
    
    state.paused = paused;
    
    this.server.broadcastToRoom(roomId, {
      type: 70,
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
  
  _startFrameLoop() {
    this.frameIntervalId = setInterval(() => {
      this._tick();
    }, this.frameInterval);
  }
  
  _tick() {
    const now = Date.now();
    
    for (const [roomId, state] of this.roomFrameStates) {
      if (state.paused) continue;
      
      const elapsed = now - state.lastFrameTime;
      if (elapsed >= this.inputTimeout) {
        this._forceAdvanceFrame(roomId, state);
      } else {
        this._tryAdvanceFrame(roomId);
      }
    }
  }
  
  _tryAdvanceFrame(roomId) {
    const state = this.roomFrameStates.get(roomId);
    if (!state || state.paused) return;
    
    if (state.pendingInputs.size >= state.expectedPlayers) {
      this._advanceFrame(roomId, state);
    }
  }
  
  _advanceFrame(roomId, state) {
    const now = Date.now();
    
    const confirmMsg = {
      type: 71,
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
    
    // 只保留最近120帧历史（6秒@20Hz），增大以支持断线重连
    if (state.frameHistory.length > 120) {
      state.frameHistory.shift();
    }

    // 缓存到重连管理器
    if (this.server.reconnectionManager) {
      this.server.reconnectionManager.cacheFrame(roomId, confirmMsg.frame_confirm_msg);
    }
    
    // 广播确认帧
    this.server.broadcastToRoom(roomId, confirmMsg);
    
    // 更新状态
    state.currentFrame++;
    state.pendingInputs.clear();
    state.lastFrameTime = now;
  }
  
  _forceAdvanceFrame(roomId, state) {
    const now = Date.now();
    
    const room = this.server.roomManager.getRoom(roomId);
    if (!room) return;
    
    // 为未提交输入的玩家生成空输入
    for (const [connId, player] of room.players) {
      if (player.disconnected) continue;
      
      if (!state.pendingInputs.has(connId)) {
        const lastInput = this._getLastInput(connId);
        state.pendingInputs.set(connId, {
          frame: state.currentFrame,
          inputData: lastInput ? lastInput.inputData : null,
          receivedAt: now,
          timeout: true,
        });
      }
    }
    
    this._advanceFrame(roomId, state);
    this._recordTimeout(roomId);
  }
  
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
   * #12 修复: _getLastInput 现在从 playerLastFrame 中实际获取上一次输入
   */
  _getLastInput(connId) {
    return this.playerLastFrame.get(connId) || null;
  }
  
  _recordTimeout(roomId) {
    // 用于监控和调试
  }
  
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
  
  destroy() {
    if (this.frameIntervalId) {
      clearInterval(this.frameIntervalId);
    }
  }
}

module.exports = { FrameSyncManager };
