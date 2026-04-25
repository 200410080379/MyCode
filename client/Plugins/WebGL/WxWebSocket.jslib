mergeInto(LibraryManager.library, {
    wx_connect_socket: function(url) {
        var urlStr = Pointer_stringify(url);
        var socketTask = wx.connectSocket({
            url: urlStr,
            success: function(res) {
                console.log('[WxWebSocket] connect success');
            },
            fail: function(err) {
                console.error('[WxWebSocket] connect fail:', err);
                // 通知 C# 层连接失败
                SendMessage('WxWebSocketBridge', 'OnSocketError', err.errMsg || 'connect_failed');
            }
        });

        var taskId = socketTask.socketTaskId || 1;

        socketTask.onOpen(function(res) {
            SendMessage('WxWebSocketBridge', 'OnSocketOpen', taskId.toString());
        });

        socketTask.onClose(function(res) {
            SendMessage('WxWebSocketBridge', 'OnSocketClose', res.reason || 'closed');
        });

        socketTask.onMessage(function(res) {
            SendMessage('WxWebSocketBridge', 'OnSocketMessage', res.data);
        });

        socketTask.onError(function(err) {
            SendMessage('WxWebSocketBridge', 'OnSocketError', err.errMsg || 'error');
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
