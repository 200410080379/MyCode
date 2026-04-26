// MiniLink 抖音小程序登录和分享桥接
// #25: 支持通过 window.__MiniLink_DouyinLoginBridgeName 自定义回调目标对象名
// #28: 补充抖音分享回调、更多抖音特有接口
// API文档: https://developer.open-douyin.com/docs/resource/zh-CN/mini-game/develop/guide/open-capacity/log-in
mergeInto(LibraryManager.library, {
    tt_login_native: function() {
        var bridgeName = (typeof window.__MiniLink_DouyinLoginBridgeName !== 'undefined')
            ? window.__MiniLink_DouyinLoginBridgeName
            : 'DouyinLoginBridge';
        tt.login({
            force: true,
            success: function(res) {
                if (res.code) {
                    SendMessage(bridgeName, 'OnTtLoginCode', res.code);
                }
            },
            fail: function(err) {
                console.error('[DouyinLogin] tt.login fail:', err);
                SendMessage(bridgeName, 'HandleLoginFailed', err.errMsg || 'login_failed');
            }
        });
    },

    tt_share_app_message: function(roomIdPtr, titlePtr, imageUrlPtr) {
        var roomId = Pointer_stringify(roomIdPtr);
        var title = Pointer_stringify(titlePtr);
        var imageUrl = Pointer_stringify(imageUrlPtr);

        // #28: 抖音分享完整实现，含回调通知
        tt.shareAppMessage({
            title: title || '来一起玩吧！',
            imageUrl: imageUrl,
            query: 'room=' + roomId,
            success: function() {
                console.log('[DouyinLogin] share success');
                // 可选：通知 C# 层分享成功
                var bridgeName = (typeof window.__MiniLink_DouyinLoginBridgeName !== 'undefined')
                    ? window.__MiniLink_DouyinLoginBridgeName
                    : 'DouyinLoginBridge';
                SendMessage(bridgeName, 'OnShareSuccess', '');
            },
            fail: function(err) {
                console.error('[DouyinLogin] share fail:', err);
                var bridgeName = (typeof window.__MiniLink_DouyinLoginBridgeName !== 'undefined')
                    ? window.__MiniLink_DouyinLoginBridgeName
                    : 'DouyinLoginBridge';
                SendMessage(bridgeName, 'OnShareFailed', err.errMsg || 'share_failed');
            }
        });
    },

    tt_get_user_info: function() {
        var bridgeName = (typeof window.__MiniLink_DouyinLoginBridgeName !== 'undefined')
            ? window.__MiniLink_DouyinLoginBridgeName
            : 'DouyinLoginBridge';
        tt.getUserInfo({
            success: function(res) {
                var userInfo = JSON.stringify(res.userInfo);
                SendMessage(bridgeName, 'OnTtUserInfo', userInfo);
            },
            fail: function(err) {
                console.error('[DouyinLogin] getUserInfo fail:', err);
            }
        });
    },

    // #28: 抖音特有 - 匿名登录
    tt_login_anonymous: function() {
        var bridgeName = (typeof window.__MiniLink_DouyinLoginBridgeName !== 'undefined')
            ? window.__MiniLink_DouyinLoginBridgeName
            : 'DouyinLoginBridge';
        if (typeof tt.login !== 'undefined') {
            tt.login({
                force: false,
                success: function(res) {
                    if (res.anonymousCode) {
                        SendMessage(bridgeName, 'OnTtAnonymousLogin', res.anonymousCode);
                    } else if (res.code) {
                        SendMessage(bridgeName, 'OnTtLoginCode', res.code);
                    }
                },
                fail: function(err) {
                    SendMessage(bridgeName, 'HandleLoginFailed', err.errMsg || 'login_failed');
                }
            });
        }
    },

    tt_show_video_ad: function() {
        var bridgeName = (typeof window.__MiniLink_DouyinLoginBridgeName !== 'undefined')
            ? window.__MiniLink_DouyinLoginBridgeName
            : 'DouyinLoginBridge';
        var rewardedVideoAd = tt.createRewardedVideoAd({
            adUnitId: 'your-ad-unit-id'
        });

        rewardedVideoAd.onClose(function(res) {
            if (res && res.isEnded) {
                SendMessage(bridgeName, 'OnVideoAdRewarded', 'true');
            } else {
                SendMessage(bridgeName, 'OnVideoAdRewarded', 'false');
            }
        });

        rewardedVideoAd.onError(function(err) {
            console.error('[DouyinLogin] video ad error:', err);
            SendMessage(bridgeName, 'OnVideoAdError', err.errMsg || 'ad_error');
        });

        rewardedVideoAd.show().catch(function() {
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
    },

    // #28: 抖音特有 - 创建更多游戏功能
    tt_create_banner_ad: function(adUnitIdPtr) {
        var adUnitId = Pointer_stringify(adUnitIdPtr);
        try {
            var bannerAd = tt.createBannerAd({
                adUnitId: adUnitId,
                style: { width: 300, top: 400 }
            });
            bannerAd.show();
        } catch (e) {
            console.error('[DouyinLogin] banner ad error:', e);
        }
    },

    tt_create_interstitial_ad: function(adUnitIdPtr) {
        var adUnitId = Pointer_stringify(adUnitIdPtr);
        try {
            var interstitialAd = tt.createInterstitialAd({ adUnitId: adUnitId });
            interstitialAd.show();
        } catch (e) {
            console.error('[DouyinLogin] interstitial ad error:', e);
        }
    }
});
