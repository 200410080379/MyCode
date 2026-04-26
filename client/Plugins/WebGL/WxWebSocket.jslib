// MiniLink 微信小程序 WebSocket 桥接
// #25: 支持通过 window.__MiniLink_WxBridgeName 自定义回调目标对象名
// 默认回调到 WxWebSocketBridge（由 WxWebSocketTransport.cs 的 Awake 设置 gameObject.name）
mergeInto(LibraryManager.library, {
    wx_connect_socket: function(url) {
        var urlStr = Pointer_stringify(url);
        var bridgeName = (typeof window.__MiniLink_WxBridgeName !== 'undefined')
            ? window.__MiniLink_WxBridgeName
            : 'WxWebSocketBridge';
        var socketTask = wx.connectSocket({
            url: urlStr,
            success: function(res) {
                console.log('[WxWebSocket] connect success');
            },
            fail: function(err) {
                console.error('[WxWebSocket] connect fail:', err);
                SendMessage(bridgeName, 'OnSocketError', err.errMsg || 'connect_failed');
            }
        });

        var taskId = socketTask.socketTaskId || 1;

        socketTask.onOpen(function(res) {
            SendMessage(bridgeName, 'OnSocketOpen', taskId.toString());
        });

        socketTask.onClose(function(res) {
            SendMessage(bridgeName, 'OnSocketClose', res.reason || 'closed');
        });

        socketTask.onMessage(function(res) {
            SendMessage(bridgeName, 'OnSocketMessage', res.data);
        });

        socketTask.onError(function(err) {
            SendMessage(bridgeName, 'OnSocketError', err.errMsg || 'error');
        });

        return taskId;
    },

    wx_close_socket: function(taskId) {
        wx.closeSocket();
    },

    wx_send_socket_message: function(taskId, dataPtr, length) {
        var data = new Uint8Array(Module.HEAPU8.buffer, dataPtr, length);
        var str = String.fromCharCode.apply(null, data);
        wx.sendSocketMessage({
            data: str,
            success: function() {},
            fail: function(err) {
                console.error('[WxWebSocket] send fail:', err);
            }
        });
    }
});
