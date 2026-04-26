using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;

namespace MiniLink
{
    /// <summary>
    /// 微信登录组件
    /// 封装微信小程序登录流程（wx.login → 服务端code2Session）
    /// 注意：此组件需要添加到场景中的 GameObject 上，名称必须为 "WechatLoginBridge"
    /// </summary>
    [AddComponentMenu("MiniLink/WechatLogin")]
    public class WechatLogin : MonoBehaviour
    {
        #region Singleton Pattern
        
        /// <summary>单例实例（用于 JSLIB 回调）</summary>
        public static WechatLogin singleton { get; private set; }
        
        private void Awake()
        {
            if (singleton != null && singleton != this)
            {
                Destroy(gameObject);
                return;
            }
            singleton = this;
            DontDestroyOnLoad(gameObject);
            gameObject.name = "WechatLoginBridge"; // 确保名称正确，用于 SendMessage
        }
        
        #endregion

        #region Events

        [Header("Events")]
        public UnityEvent<string> OnLoginSuccess;
        public UnityEvent<string> OnLoginFailed;

        #endregion

        #region Properties

        /// <summary>是否已登录</summary>
        public bool IsLoggedIn { get; private set; }

        /// <summary>当前token</summary>
        public string Token { get; private set; }

        /// <summary>openid</summary>
        public string OpenId { get; private set; }

        /// <summary>昵称</summary>
        public string Nickname { get; set; } = "玩家";

        /// <summary>头像URL</summary>
        public string AvatarUrl { get; set; } = "";

        private bool loginInProgress;

        #endregion

        #region Public API

        /// <summary>
        /// 发起微信登录
        /// </summary>
        public void Login()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WxLogin();
#else
            // 编辑器模式：模拟登录
            Debug.Log("[WechatLogin] 编辑器模式，模拟登录");
            StartCoroutine(MockLogin());
#endif
        }

        /// <summary>
        /// 登出
        /// </summary>
        public void Logout()
        {
            IsLoggedIn = false;
            Token = null;
            OpenId = null;
            loginInProgress = false;
            Debug.Log("[WechatLogin] 已登出");
        }

        #endregion

        #region 微信登录流程

        /// <summary>
        /// 调用微信登录API
        /// </summary>
        private void WxLogin()
        {
            // 微信小程序环境调用 wx.login
            // 通过JSLIB桥接
            WxLoginNative();
        }

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void wx_login_native();

        private void WxLoginNative()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            wx_login_native();
#else
            StartCoroutine(MockLogin());
#endif
        }

        /// <summary>
        /// 微信登录回调（由JSLIB调用）
        /// </summary>
        public void OnWxLoginCode(string code)
        {
            Debug.Log($"[WechatLogin] 获取到code: {code}");

            // 发送code到服务端换取openid和session_key
            SendCodeToServer(code);
        }

        /// <summary>
        /// 将微信code发送到服务端
        /// </summary>
        private void SendCodeToServer(string code)
        {
            if (!NetworkClient.isConnected)
            {
                OnLoginFailed?.Invoke("未连接到服务器");
                return;
            }

            var msg = new System.Collections.Generic.Dictionary<string, object>
            {
                ["type"] = 50,
                ["seq"] = NetworkClient.connection.nextSeq(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["wx_login_req"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["code"] = code,
                    ["nickname"] = Nickname,
                    ["avatar_url"] = AvatarUrl,
                }
            };

            loginInProgress = true;
            NetworkClient.SendJson(msg);

            // 等待服务端响应（通过消息处理）
            StartCoroutine(WaitForLoginResponse());
        }

        private IEnumerator WaitForLoginResponse()
        {
            float timeout = 10f;
            float elapsed = 0f;

            while (loginInProgress && !IsLoggedIn && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (loginInProgress && !IsLoggedIn)
            {
                HandleLoginFailed("登录超时");
            }
        }

        /// <summary>
        /// 服务端登录成功回调
        /// </summary>
        public void OnServerLoginSuccess(string token, string openid, string sessionKey)
        {
            Token = token;
            OpenId = openid;
            IsLoggedIn = true;
            loginInProgress = false;

            Debug.Log($"[WechatLogin] 登录成功: openid={openid}");
            OnLoginSuccess?.Invoke(openid);
        }

        public void HandleLoginFailed(string error)
        {
            loginInProgress = false;
            Debug.LogError($"[WechatLogin] 登录失败: {error}");
            OnLoginFailed?.Invoke(error);
        }

        public void OnWxUserInfo(string userInfoJson)
        {
            var userInfo = MiniJson.Deserialize(userInfoJson) as System.Collections.Generic.Dictionary<string, object>;
            if (userInfo == null) return;

            if (userInfo.TryGetValue("nickName", out var nickname) && nickname != null)
            {
                Nickname = nickname.ToString();
            }

            if (userInfo.TryGetValue("avatarUrl", out var avatar) && avatar != null)
            {
                AvatarUrl = avatar.ToString();
            }
        }

        #endregion

        #region 微信分享

        /// <summary>
        /// 分享到微信好友（邀请加入房间）
        /// </summary>
        public void ShareToFriend(string roomId, string title = "", string imageUrl = "")
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WxShare(roomId, title, imageUrl);
#else
            Debug.Log($"[WechatLogin] 模拟分享: room={roomId}");
#endif
        }

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void wx_share_app_message(string roomId, string title, string imageUrl);

        private void WxShare(string roomId, string title, string imageUrl)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            wx_share_app_message(roomId, title ?? "来一起玩吧！", imageUrl ?? "");
#endif
        }

        #endregion

        #region Mock Login (Editor)

        private IEnumerator MockLogin()
        {
            yield return new WaitForSeconds(0.5f);

            string mockOpenId = $"dev_player_{UnityEngine.Random.Range(1000, 9999)}";
            string mockToken = $"mock_token_{mockOpenId}";

            Token = mockToken;
            OpenId = mockOpenId;
            IsLoggedIn = true;
            loginInProgress = false;

            Debug.Log($"[WechatLogin] 模拟登录成功: {mockOpenId}");
            OnLoginSuccess?.Invoke(mockOpenId);
        }

        #endregion
    }
}

/*
// JSLIB 文件 (Plugins/WebGL/WechatLogin.jslib):
mergeInto(LibraryManager.library, {
    wx_login_native: function() {
        wx.login({
            success: function(res) {
                if (res.code) {
                    var code = res.code;
                    var gameObjectName = 'WechatLogin';
                    var methodName = 'OnWxLoginCode';
                    SendMessage(gameObjectName, methodName, code);
                }
            },
            fail: function(err) {
                console.error('[WechatLogin] wx.login fail:', err);
            }
        });
    },
    wx_share_app_message: function(roomIdPtr, titlePtr, imageUrlPtr) {
        var roomId = Pointer_stringify(roomIdPtr);
        var title = Pointer_stringifyPtr(titlePtr);
        var imageUrl = Pointer_stringify(imageUrlPtr);

        wx.shareAppMessage({
            title: title || '来一起玩吧！',
            imageUrl: imageUrl,
            query: 'room=' + roomId,
        });
    }
});
*/
