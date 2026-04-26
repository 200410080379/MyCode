// MiniLink 微信小程序登录和分享桥接
// #25: 支持通过 window.__MiniLink_WechatLoginBridgeName 自定义回调目标对象名
// 默认回调到 WechatLoginBridge（由 WechatLogin.cs 的 Awake 设置 gameObject.name）
mergeInto(LibraryManager.library, {
    wx_login_native: function() {
        var bridgeName = (typeof window.__MiniLink_WechatLoginBridgeName !== 'undefined')
            ? window.__MiniLink_WechatLoginBridgeName
            : 'WechatLoginBridge';
        wx.login({
            success: function(res) {
                if (res.code) {
                    SendMessage(bridgeName, 'OnWxLoginCode', res.code);
                }
            },
            fail: function(err) {
                console.error('[WechatLogin] wx.login fail:', err);
                SendMessage(bridgeName, 'HandleLoginFailed', err.errMsg || 'login_failed');
            }
        });
    },

    wx_share_app_message: function(roomIdPtr, titlePtr, imageUrlPtr) {
        var roomId = Pointer_stringify(roomIdPtr);
        var title = Pointer_stringify(titlePtr);
        var imageUrl = Pointer_stringify(imageUrlPtr);

        wx.shareAppMessage({
            title: title || '来一起玩吧！',
            imageUrl: imageUrl,
            query: 'room=' + roomId,
            success: function() {
                console.log('[WechatLogin] share success');
            },
            fail: function(err) {
                console.error('[WechatLogin] share fail:', err);
            }
        });
    },

    wx_get_user_info: function() {
        var bridgeName = (typeof window.__MiniLink_WechatLoginBridgeName !== 'undefined')
            ? window.__MiniLink_WechatLoginBridgeName
            : 'WechatLoginBridge';
        wx.getUserInfo({
            success: function(res) {
                var userInfo = JSON.stringify(res.userInfo);
                SendMessage(bridgeName, 'OnWxUserInfo', userInfo);
            },
            fail: function(err) {
                console.error('[WechatLogin] getUserInfo fail:', err);
            }
        });
    },

    wx_show_toast: function(titlePtr, iconPtr, duration) {
        var title = Pointer_stringify(titlePtr);
        var icon = Pointer_stringify(iconPtr);
        
        wx.showToast({
            title: title,
            icon: icon || 'none',
            duration: duration || 2000
        });
    },

    wx_vibrate_short: function() {
        wx.vibrateShort({
            success: function() {},
            fail: function() {}
        });
    },

    wx_vibrate_long: function() {
        wx.vibrateLong({
            success: function() {},
            fail: function() {}
        });
    }
});
