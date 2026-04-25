/**
 * MiniLink MessageHandler - 消息路由与处理
 * 参考 Mirror 的 NetworkMessage 消息分发体系
 */
class MessageHandler {
  constructor(server) {
    this.server = server;

    // 消息类型→处理函数映射表
    this.handlers = {
      1:  this._onHandshakeReq.bind(this),
      3:  this._onHeartbeat.bind(this),
      10: this._onRoomCreate.bind(this),
      11: this._onRoomJoin.bind(this),
      12: this._onRoomLeave.bind(this),
      14: this._onRoomList.bind(this),
      15: this._onRoomReady.bind(this),
      16: this._onRoomStart.bind(this),
      18: this._onRoomMatch.bind(this),
      20: this._onSyncVar.bind(this),
      22: this._onSnapshot.bind(this),
      23: this._onInputFrame.bind(this),
      30: this._onCommand.bind(this),
      50: this._onWxLogin.bind(this),
      51: this._onWxShare.bind(this),
      // 帧同步消息
      70: this._onFramePause.bind(this),
      72: this._onFrameInput.bind(this),
      // 断线重连消息
      80: this._onReconnectState.bind(this),
      82: this._onReconnectRequest.bind(this),
      // 延迟补偿（时间同步）
      90: this._onTimeSync.bind(this),
    };
  }

  /**
   * 处理收到的消息
   */
  handle(conn, rawData) {
    let msg;
    try {
      msg = JSON.parse(rawData);
    } catch (err) {
      console.warn(`[MessageHandler] 无效消息 from ${conn.connId}`);
      return;
    }

    // 处理批量消息
    if (Array.isArray(msg)) {
      for (const m of msg) {
        this._dispatch(conn, m);
      }
    } else {
      this._dispatch(conn, msg);
    }
  }

  /**
   * 分发单条消息
   */
  _dispatch(conn, msg) {
    const msgType = msg.type;
    if (!msgType) return;

    const handler = this.handlers[msgType];
    if (handler) {
      handler(conn, msg);
    } else {
      console.warn(`[MessageHandler] 未知消息类型: ${msgType}`);
    }
  }

  // ==================== 连接/认证 ====================

  async _onHandshakeReq(conn, msg) {
    const req = msg.handshake_req;
    if (!req) return;

    conn.isAuthenticated = true;
    conn.playerId = req.client_id || conn.connId;

    conn.send({
      type: 2, // MSG_TYPE_HANDSHAKE_RESP
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      handshake_resp: {
        success: true,
        session_id: conn.sessionId,
        server_time: new Date().toISOString(),
        reconnect_ttl: this.server.reconnectTTL / 1000,
      },
    });
  }

  _onHeartbeat(conn, msg) {
    const hb = msg.heartbeat;
    const clientTime = hb ? hb.client_time : Date.now();
    const pingMs = hb ? hb.ping_ms : 0;

    conn.updateHeartbeat(pingMs);

    // 回复心跳
    conn.send({
      type: 3,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      heartbeat: {
        client_time: clientTime,
        ping_ms: Date.now() - clientTime,
      },
    });
  }

  // ==================== 房间管理 ====================

  _onRoomCreate(conn, msg) {
    const req = msg.room_create_req;
    const result = this.server.roomManager.createRoom(conn.connId, req);

    if (result.error) {
      conn.send(this._makeError(result.error, result.message));
      return;
    }

    // 通知创建者
    conn.send({
      type: 17, // MSG_TYPE_ROOM_STATE
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      room_state_notify: result.room,
    });
  }

  _onRoomJoin(conn, msg) {
    const req = msg.room_join_req;
    const result = this.server.roomManager.joinRoom(
      conn.connId, req.room_id, req.password
    );

    if (result.error) {
      conn.send(this._makeError(result.error, result.message));
      return;
    }

    // 通知房间内所有人
    this.server.broadcastToRoom(result.room_id, {
      type: 17,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      room_state_notify: result.room,
    });
  }

  _onRoomLeave(conn, msg) {
    const result = this.server.roomManager.leaveRoom(conn.connId);

    if (result.error) {
      conn.send(this._makeError(result.error, result.message));
      return;
    }

    // 通知房间内其他人
    if (!result.dismissed && result.room) {
      this.server.broadcastToRoom(result.room_id, {
        type: 17,
        seq: this.server.nextSeq(),
        timestamp: Date.now(),
        room_state_notify: result.room,
      });
    }
  }

  _onRoomList(conn, msg) {
    const req = msg.room_list_req || {};
    const result = this.server.roomManager.getRoomList(
      req.page || 1,
      req.page_size || 20
    );

    conn.send({
      type: 14,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      // 复用 room_list_resp 结构
      room_list_resp: result,
    });
  }

  _onRoomReady(conn, msg) {
    const req = msg.room_ready_req;
    const result = this.server.roomManager.setReady(
      conn.connId, req.ready
    );

    if (result.error) {
      conn.send(this._makeError(result.error));
      return;
    }

    // 广播房间状态更新
    this.server.broadcastToRoom(result.room_id, {
      type: 17,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      room_state_notify: result.room,
    });
  }

  _onRoomStart(conn, msg) {
    const result = this.server.roomManager.startGame(conn.connId);

    if (result.error) {
      conn.send(this._makeError(result.error, result.message));
      return;
    }

    // 广播游戏开始
    this.server.broadcastToRoom(result.room_id, {
      type: 17,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      room_state_notify: result.room,
    });

    // 通知客户端生成游戏对象
    const room = this.server.roomManager.getRoom(result.room_id);
    if (room) {
      for (const [playerConnId, player] of room.players) {
        this.server.sendTo(playerConnId, {
          type: 40, // MSG_TYPE_SPAWN
          seq: this.server.nextSeq(),
          timestamp: Date.now(),
          spawn_msg: {
            net_id: this.server.nextSeq(),
            prefab_hash: 0, // 由业务层决定
            owner_conn_id: playerConnId,
            initial_state: null,
          },
        });
      }
    }
  }

  _onRoomMatch(conn, msg) {
    const req = msg.room_match_req;
    const result = this.server.roomManager.match(conn.connId, req);

    if (result.error) {
      conn.send(this._makeError(result.error));
      return;
    }

    // 通知匹配结果
    this.server.broadcastToRoom(result.room_id, {
      type: 17,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      room_state_notify: result.room,
    });
  }

  // ==================== 同步 ====================

  _onSyncVar(conn, msg) {
    this.server.syncManager.handleSyncVarUpdate(
      conn.connId, msg.sync_var_msg
    );
  }

  _onSnapshot(conn, msg) {
    this.server.syncManager.receiveSnapshot(
      conn.connId, msg.snapshot_msg
    );
  }

  _onInputFrame(conn, msg) {
    this.server.syncManager.receiveInputFrame(
      conn.connId, msg.input_frame_msg
    );
  }

  // ==================== RPC ====================

  _onCommand(conn, msg) {
    const cmd = msg.command_msg;
    if (!cmd) return;

    // 验证权限
    if (cmd.requires_auth && !conn.isAuthenticated) {
      conn.send(this._makeError('AUTH_FAILED', '未认证'));
      return;
    }

    // Command 在服务端执行，通过 ClientRpc 返回结果给客户端
    // 具体业务逻辑由上层注册的 handler 处理
    // 这里只做路由 - 注意：如需要事件机制，请在 MiniLinkServer 中添加 EventEmitter
    // this.server.emit('command', { conn, cmd });

    // 简化实现：直接回复（实际项目应注册 Command 处理器）
    console.log(`[MessageHandler] 收到Command: ${cmd.method_hash} from ${conn.connId}`);
  }

  // ==================== 微信 ====================

  async _onWxLogin(conn, msg) {
    const req = msg.wx_login_req;
    if (!req || !req.code) {
      conn.send(this._makeError('AUTH_FAILED', '缺少code'));
      return;
    }

    try {
      const wxResult = await this.server.wechatAdapter.login(req.code);
      conn.isAuthenticated = true;
      conn.playerId = wxResult.openid;
      conn.nickname = req.nickname || '玩家';
      conn.avatarUrl = req.avatar_url || '';

      conn.send({
        type: 61, // MSG_TYPE_WX_LOGIN_RESP
        seq: this.server.nextSeq(),
        timestamp: Date.now(),
        wx_login_resp: {
          success: true,
          token: wxResult.token,
          openid: wxResult.openid,
          session_key: wxResult.session_key,
        },
      });
    } catch (err) {
      conn.send({
        type: 61,
        seq: this.server.nextSeq(),
        timestamp: Date.now(),
        wx_login_resp: {
          success: false,
          token: '',
          openid: '',
          session_key: '',
        },
      });
    }
  }

  _onWxShare(conn, msg) {
    // 微信分享在客户端完成，服务端只记录
    const req = msg.wx_share_req;
    console.log(`[MessageHandler] 分享: room=${req.room_id} by ${conn.connId}`);
  }

  // ==================== 帧同步 ====================

  _onFramePause(conn, msg) {
    const req = msg.frame_pause_msg;
    if (!req) return;
    // 只有主机可以暂停
    const room = conn.roomId ? this.server.roomManager.getRoom(conn.roomId) : null;
    if (!room) return;
    const player = room.players.get(conn.connId);
    if (!player || !player.isHost) return;
    this.server.frameSyncManager.setPaused(conn.roomId, req.paused);
  }

  _onFrameInput(conn, msg) {
    // 帧同步输入由 FrameSyncManager 处理
    this.server.frameSyncManager.receiveInput(conn.connId, msg);
  }

  // ==================== 断线重连 ====================

  _onReconnectState(conn, msg) {
    // 客户端查询重连状态
    const state = this.server.reconnectionManager.isWaitingReconnect(conn.connId);
    conn.send({
      type: 80,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      player_reconnect_msg: {
        conn_id: conn.connId,
        state: state ? 'waiting' : 'none',
      },
    });
  }

  _onReconnectRequest(conn, msg) {
    const req = msg.reconnect_req;
    if (!req) return;

    // 通过 playerId 查找旧连接
    const oldState = this.server.reconnectionManager.findByPlayerId(req.player_id);
    if (!oldState) {
      conn.send({
        type: 81,
        seq: this.server.nextSeq(),
        timestamp: Date.now(),
        reconnect_result_msg: {
          success: false,
          reason: 'no_saved_state',
        },
      });
      return;
    }

    const result = this.server.reconnectionManager.tryReconnect(
      oldState.connId, conn.connId, req.room_id
    );

    if (!result.success) {
      conn.send({
        type: 81,
        seq: this.server.nextSeq(),
        timestamp: Date.now(),
        reconnect_result_msg: {
          success: false,
          reason: result.reason,
        },
      });
    }
    // 成功的情况在 tryReconnect 中已发送
  }

  // ==================== 延迟补偿（时间同步）====================

  _onTimeSync(conn, msg) {
    const req = msg.time_sync_req;
    if (!req) return;

    const serverReceiveTime = Date.now();

    conn.send({
      type: 91, // MSG_TYPE_TIME_SYNC_RESP
      seq: this.server.nextSeq(),
      timestamp: serverReceiveTime,
      time_sync_resp: {
        client_send_time: req.client_send_time,
        server_receive_time: serverReceiveTime,
        server_send_time: Date.now(),
      },
    });
  }

  // ==================== 工具方法 ====================

  _makeError(code, message = '') {
    return {
      type: 99,
      seq: this.server.nextSeq(),
      timestamp: Date.now(),
      error_msg: { code, message },
    };
  }
}

module.exports = { MessageHandler };
