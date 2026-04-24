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
            }
        });

        var taskId = socketTask.socketTaskId || 1;

        socketTask.onOpen(function(res) {
            SendMessage('NetworkClient', 'OnSocketOpen', taskId.toString());
        });

        socketTask.onClose(function(res) {
            SendMessage('NetworkClient', 'OnSocketClose', res.reason || 'closed');
        });

        socketTask.onMessage(function(res) {
            SendMessage('NetworkClient', 'OnSocketMessage', res.data);
        });

        socketTask.onError(function(err) {
            SendMessage('NetworkClient', 'OnSocketError', err.errMsg || 'error');
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
