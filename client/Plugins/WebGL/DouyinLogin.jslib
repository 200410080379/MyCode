// 抖音小程序登录和分享桥接
// API文档: https://developer.open-douyin.com/docs/resource/zh-CN/mini-game/develop/guide/open-capacity/log-in
mergeInto(LibraryManager.library, {
    tt_login_native: function() {
        tt.login({
            force: true,
            success: function(res) {
                if (res.code) {
                    SendMessage('DouyinLoginBridge', 'OnTtLoginCode', res.code);
                }
            },
            fail: function(err) {
                console.error('[DouyinLogin] tt.login fail:', err);
                SendMessage('DouyinLoginBridge', 'OnLoginFailed', err.errMsg || 'login_failed');
            }
        });
    },

    tt_share_app_message: function(roomIdPtr, titlePtr, imageUrlPtr) {
        var roomId = Pointer_stringify(roomIdPtr);
        var title = Pointer_stringify(titlePtr);
        var imageUrl = Pointer_stringify(imageUrlPtr);

        tt.shareAppMessage({
            title: title || '来一起玩吧！',
            imageUrl: imageUrl,
            query: 'room=' + roomId,
            success: function() {
                console.log('[DouyinLogin] share success');
            },
            fail: function(err) {
                console.error('[DouyinLogin] share fail:', err);
            }
        });
    },

    tt_get_user_info: function() {
        tt.getUserInfo({
            success: function(res) {
                var userInfo = JSON.stringify(res.userInfo);
                SendMessage('DouyinLoginBridge', 'OnTtUserInfo', userInfo);
            },
            fail: function(err) {
                console.error('[DouyinLogin] getUserInfo fail:', err);
            }
        });
    },

    tt_show_video_ad: function() {
        var rewardedVideoAd = tt.createRewardedVideoAd({
            adUnitId: 'your-ad-unit-id' // 需要替换为真实的广告单元ID
        });

        rewardedVideoAd.onClose(function(res) {
            if (res && res.isEnded) {
                SendMessage('DouyinLoginBridge', 'OnVideoAdRewarded', 'true');
            } else {
                console.log('[DouyinLogin] video ad not completed');
            }
        });

        rewardedVideoAd.onError(function(err) {
            console.error('[DouyinLogin] video ad error:', err);
        });

        rewardedVideoAd.show().catch(function() {
            // 广告未加载完成，先加载再显示
            rewardedVideoAd.load().then(function() {
                return rewardedVideoAd.show();
            });
        });
    },

    tt_vibrate_short: function() {
        tt.vibrateShort({ success: function() {}, fail: function() {} });
    },

    tt_vibrate_long: function() {
        tt.vibrateLong({ success: function() {}, fail: function() {} });
    },

    tt_show_toast: function(titlePtr, iconPtr, duration) {
        var title = Pointer_stringify(titlePtr);
        var icon = Pointer_stringify(iconPtr);
        
        tt.showToast({
            title: title,
            icon: icon || 'none',
            duration: duration || 2000
        });
    }
});
