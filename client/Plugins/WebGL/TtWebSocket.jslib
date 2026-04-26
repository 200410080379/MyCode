// MiniLink 抖音小程序 WebSocket 桥接
// #25: 支持通过 window.__MiniLink_TtBridgeName 自定义回调目标对象名
// API文档: https://developer.open-douyin.com/docs/resource/zh-CN/mini-game/develop/server/connect
mergeInto(LibraryManager.library, {
    tt_connect_socket: function(url) {
        var urlStr = Pointer_stringify(url);
        var bridgeName = (typeof window.__MiniLink_TtBridgeName !== 'undefined')
            ? window.__MiniLink_TtBridgeName
            : 'TtWebSocketBridge';
        
        var socketTask = tt.connectSocket({
            url: urlStr,
            success: function(res) {
                console.log('[TtWebSocket] connect success');
            },
            fail: function(err) {
                console.error('[TtWebSocket] connect fail:', err);
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

    tt_close_socket: function(taskId) {
        tt.closeSocket();
    },

    tt_send_socket_message: function(taskId, dataPtr, length) {
        var data = new Uint8Array(Module.HEAPU8.buffer, dataPtr, length);
        var str = String.fromCharCode.apply(null, data);
        tt.sendSocketMessage({
            data: str,
            success: function() {},
            fail: function(err) {
                console.error('[TtWebSocket] send fail:', err);
            }
        });
    }
});
