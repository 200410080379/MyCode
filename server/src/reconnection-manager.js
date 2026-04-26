/**
 * MiniLink ReconnectionManager - 断线重连管理器（服务端）
 * 
 * 核心功能：
 * 1. 连接断开时保留玩家状态（不立即销毁）
 * 2. 记录断开期间的帧数据
 * 3. 玩家重连后恢复状态并补发丢失的帧
 * 4. 超时未重连则清理资源
 */
class ReconnectionManager {
  constructor(server) {
    this.server = server;
    
    // 断线玩家缓存
    // connId -> ReconnectionState
    this.reconnectionStates = new Map();
    
    // 配置
    this.reconnectTimeout = 30000; // 30秒重连窗口
    this.maxFrameCache = 120; // 缓存最多120帧（6秒@20Hz）
    this.cleanupInterval = 5000; // 5秒清理一次
    this.reconnectSuccessCount = 0;
    this.reconnectFailCount = 0;
    
    // 启动清理定时器
    this._startCleanup();
  }
  
  /**
   * 玩家断开时调用
   * 保存状态并开始重连倒计时
   */
  onPlayerDisconnect(connId) {
    const conn = this.server.connections.get(connId);
    if (!conn) return;
    
    const roomId = conn.roomId;
    if (!roomId) return;
    
    // 获取玩家当前状态
    const room = this.server.roomManager.getRoom(roomId);
    if (!room) return;
    
    const player = room.players.get(connId);
    if (!player) return;
    
    // 获取当前帧号
    const frameState = this.server.frameSyncManager?.roomFrameStates?.get(roomId);
    const currentFrame = frameState ? frameState.currentFrame : 0;
    
    // 保存重连状态
    const reconnState = {
      connId,
      playerId: conn.playerId,
      roomId,
      playerNetId: conn.playerNetId,
      nickname: conn.nickname,
      avatarUrl: conn.avatarUrl,
      isReady: player.is_ready,
      isHost: player.is_host,
      
      // 断线时状态快照
      disconnectedAt: Date.now(),
      disconnectedFrame: currentFrame,
      
      // 帧缓存（断线期间收集）
      frameCache: [],
      
      // 标记房间中的玩家为断线状态（而非移除）
      active: true,
    };
    
    this.reconnectionStates.set(connId, reconnState);
    
    // 标记玩家为断线状态
    player.disconnected = true;
    player.reconnectDeadline = Date.now() + this.reconnectTimeout;
    
    // 通知房间内其他玩家
    this.server.broadcastToRoom(roomId, {
      type: 80, // MSG_TYPE_PLAYER_RECONNECT_STATE
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      player_reconnect_msg: {
        conn_id: connId,
        player_id: conn.playerId,
        state: 'disconnected',
        timeout: Math.floor(this.reconnectTimeout / 1000),
      },
    }, connId);
    
    console.log(`[Reconnect] 玩家 ${conn.playerId} 断线，等待重连 (roomId=${roomId}, frame=${currentFrame})`);
  }
  
  /**
   * 玩家尝试重连
   * 返回重连结果
   */
  tryReconnect(connId, newConnId, roomId) {
    const reconnState = this.reconnectionStates.get(connId);
    if (!reconnState) {
      return { success: false, reason: 'no_saved_state' };
    }
    
    // 检查是否超时
    if (Date.now() > reconnState.disconnectedAt + this.reconnectTimeout) {
      this._cleanupReconnection(connId);
      return { success: false, reason: 'timeout' };
    }
    
    // 检查房间是否还存在
    const room = this.server.roomManager.getRoom(reconnState.roomId);
    if (!room) {
      this._cleanupReconnection(connId);
      return { success: false, reason: 'room_not_found' };
    }
    
    // 恢复玩家状态
    const newConn = this.server.connections.get(newConnId);
    if (!newConn) {
      return { success: false, reason: 'new_connection_not_found' };
    }
    
    // 恢复连接属性
    newConn.playerId = reconnState.playerId;
    newConn.playerNetId = reconnState.playerNetId;
    newConn.nickname = reconnState.nickname;
    newConn.avatarUrl = reconnState.avatarUrl;
    newConn.roomId = reconnState.roomId;
    newConn.isAuthenticated = true;
    newConn.isReconnecting = true;
    
    // 更新房间中的玩家映射
    const existingPlayer = room.players.get(connId);
    room.players.delete(connId);
    room.players.set(newConnId, {
      ...(existingPlayer || {}),
      conn_id: newConnId,
      player_id: reconnState.playerId,
      nickname: reconnState.nickname,
      avatar_url: reconnState.avatarUrl,
      is_ready: reconnState.isReady,
      is_host: reconnState.isHost,
      disconnected: false,
      disconnectedAt: null,
      reconnectDeadline: null,
    });
    this.server.roomManager.playerRoomMap.delete(connId);
    this.server.roomManager.playerRoomMap.set(newConnId, reconnState.roomId);

    if (room.hostId === connId) {
      room.hostId = newConnId;
    }

    this.server.connections.delete(connId);
    
    // 通知房间内其他玩家
    this.server.broadcastToRoom(reconnState.roomId, {
      type: 80,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      player_reconnect_msg: {
        conn_id: newConnId,
        player_id: reconnState.playerId,
        state: 'reconnected',
      },
    });
    
    // 发送重连成功消息给重连玩家
    const frameState = this.server.frameSyncManager?.roomFrameStates?.get(reconnState.roomId);
    const currentFrame = frameState ? frameState.currentFrame : 0;
    
    newConn.send({
      type: 81, // MSG_TYPE_RECONNECT_RESULT
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      reconnect_result_msg: {
        success: true,
        room_id: reconnState.roomId,
        player_id: reconnState.playerId,
        current_frame: currentFrame,
        disconnected_frame: reconnState.disconnectedFrame,
        missed_frames: reconnState.frameCache,
        room_state: this.server.roomManager._roomToNotify(room),
      },
    });
    
    // 清理重连状态
    this.reconnectionStates.delete(connId);
    this.reconnectSuccessCount++;
    
    console.log(`[Reconnect] 玩家 ${reconnState.playerId} 重连成功 (missed ${reconnState.frameCache.length} frames)`);
    
    return { success: true, roomId: reconnState.roomId };
  }
  
  /**
   * 收集断线期间的帧（由帧同步管理器调用）
   */
  cacheFrame(roomId, frameData) {
    for (const [connId, state] of this.reconnectionStates) {
      if (state.roomId === roomId) {
        state.frameCache.push(frameData);
        
        // 限制缓存大小
        if (state.frameCache.length > this.maxFrameCache) {
          state.frameCache.shift();
        }
      }
    }
  }
  
  /**
   * 检查是否在等待重连
   */
  isWaitingReconnect(connId) {
    return this.reconnectionStates.has(connId);
  }
  
  /**
   * 通过playerId查找重连状态
   */
  findByPlayerId(playerId) {
    for (const [connId, state] of this.reconnectionStates) {
      if (state.playerId === playerId) {
        return state;
      }
    }
    return null;
  }
  
  // ==================== 内部方法 ====================
  
  /**
   * 序列化房间状态（用于重连恢复）
   */
  _serializeRoomState(room) {
    const players = [];
    for (const [connId, player] of room.players) {
      players.push({
        player_id: player.player_id,
        nickname: player.nickname,
        avatar_url: player.avatar_url,
        slot_index: player.slot_index,
        connected: !player.disconnected,
        is_ready: player.is_ready,
        is_host: player.is_host,
      });
    }
    
    return {
      state: room.state,
      players,
      player_count: room.players.size,
    };
  }
  
  /**
   * 清理重连状态（超时）
   */
  _cleanupReconnection(connId) {
    const state = this.reconnectionStates.get(connId);
    if (!state) return;
    
    // 从房间中移除玩家
    const room = this.server.roomManager.getRoom(state.roomId);
    if (room) {
      // 通知其他玩家
      this.server.broadcastToRoom(state.roomId, {
        type: 80,
        seq: this.server.nextSeq(),
        timestamp: Date.now(),
        player_reconnect_msg: {
          conn_id: connId,
          player_id: state.playerId,
          state: 'timeout',
        },
      });

      const leaveResult = this.server.roomManager.leaveRoom(connId);
      if (!leaveResult.error && !leaveResult.dismissed && leaveResult.room) {
        this.server.broadcastToRoom(state.roomId, {
          type: 17,
          seq: this.server.nextSeq(),
          timestamp: Date.now(),
          room_state_notify: leaveResult.room,
        });
      }
    }
    
    this.reconnectionStates.delete(connId);
    this.reconnectFailCount++;
    
    console.log(`[Reconnect] 玩家 ${state.playerId} 重连超时`);
  }
  
  /**
   * 启动清理定时器
   */
  _startCleanup() {
    this.cleanupTimerId = setInterval(() => {
      const now = Date.now();
      
      for (const [connId, state] of this.reconnectionStates) {
        if (now > state.disconnectedAt + this.reconnectTimeout) {
          this._cleanupReconnection(connId);
        }
      }
    }, this.cleanupInterval);
  }
  
  /**
   * 获取统计信息
   */
  getStats() {
    return {
      waitingReconnect: this.reconnectionStates.size,
      successCount: this.reconnectSuccessCount,
      failCount: this.reconnectFailCount,
      successRate: this.reconnectSuccessCount + this.reconnectFailCount > 0
        ? (this.reconnectSuccessCount / (this.reconnectSuccessCount + this.reconnectFailCount) * 100).toFixed(1) + '%'
        : 'N/A',
    };
  }
  
  /**
   * 清理
   */
  destroy() {
    if (this.cleanupTimerId) {
      clearInterval(this.cleanupTimerId);
    }
  }
}

module.exports = { ReconnectionManager };
