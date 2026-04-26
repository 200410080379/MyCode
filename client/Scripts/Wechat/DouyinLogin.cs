using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;

namespace MiniLink
{
    /// <summary>
    /// 抖音小程序登录组件
    /// 封装抖音小游戏登录流程（tt.login → 服务端code2Session）
    /// 注意：此组件需要添加到场景中的 GameObject 上，名称必须为 "DouyinLoginBridge"
    /// </summary>
    [AddComponentMenu("MiniLink/DouyinLogin")]
    public class DouyinLogin : MonoBehaviour
    {
        #region Singleton Pattern
        
        public static DouyinLogin singleton { get; private set; }
        
        private void Awake()
        {
            if (singleton != null && singleton != this)
            {
                Destroy(gameObject);
                return;
            }
            singleton = this;
            DontDestroyOnLoad(gameObject);
            gameObject.name = "DouyinLoginBridge";
        }
        
        #endregion

        #region Events

        [Header("Events")]
        public UnityEvent<string> OnLoginSuccess;
        public UnityEvent<string> OnLoginFailed;

        #endregion

        #region Properties

        public bool IsLoggedIn { get; private set; }
        public string Token { get; private set; }
        public string OpenId { get; private set; }
        public string Nickname { get; set; } = "玩家";
        public string AvatarUrl { get; set; } = "";
        private bool loginInProgress;

        #endregion

        #region Public API

        public void Login()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            TtLoginNative();
#else
            Debug.Log("[DouyinLogin] 编辑器模式，模拟登录");
            StartCoroutine(MockLogin());
#endif
        }

        public void Logout()
        {
            IsLoggedIn = false;
            Token = null;
            OpenId = null;
            loginInProgress = false;
            Debug.Log("[DouyinLogin] 已登出");
        }

        #endregion

        #region 抖音登录流程

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void tt_login_native();

        private void TtLoginNative()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            tt_login_native();
#else
            StartCoroutine(MockLogin());
#endif
        }

        /// <summary>抖音登录回调（由JSLIB调用）</summary>
        public void OnTtLoginCode(string code)
        {
            Debug.Log($"[DouyinLogin] 获取到code: {code}");
            SendCodeToServer(code, "douyin");
        }

        /// <summary>匿名登录回调（抖音支持匿名登录）</summary>
        public void OnTtAnonymousLogin(string anonymousCode)
        {
            Debug.Log($"[DouyinLogin] 匿名登录: {anonymousCode}");
            SendCodeToServer(anonymousCode, "douyin_anonymous");
        }

        private void SendCodeToServer(string code, string platform)
        {
            if (!NetworkClient.isConnected)
            {
                OnLoginFailed?.Invoke("未连接到服务器");
                return;
            }

            var msg = new System.Collections.Generic.Dictionary<string, object>
            {
                ["type"] = 50,
                ["seq"] = NetworkClient.connection?.nextSeq() ?? 1,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["wx_login_req"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["code"] = code,
                    ["platform"] = platform, // 标识抖音平台
                    ["nickname"] = Nickname,
                    ["avatar_url"] = AvatarUrl,
                }
            };

            loginInProgress = true;
            NetworkClient.SendJson(msg);
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

        public void OnServerLoginSuccess(string token, string openid, string sessionKey)
        {
            Token = token;
            OpenId = openid;
            IsLoggedIn = true;
            loginInProgress = false;

            Debug.Log($"[DouyinLogin] 登录成功: openid={openid}");
            OnLoginSuccess?.Invoke(openid);
        }

        public void HandleLoginFailed(string error)
        {
            loginInProgress = false;
            Debug.LogError($"[DouyinLogin] 登录失败: {error}");
            OnLoginFailed?.Invoke(error);
        }

        public void OnTtUserInfo(string userInfoJson)
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

        #region 抖音分享

        public void ShareToFriend(string roomId, string title = "", string imageUrl = "")
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            TtShare(roomId, title, imageUrl);
#else
            Debug.Log($"[DouyinLogin] 模拟分享: room={roomId}");
#endif
        }

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void tt_share_app_message(string roomId, string title, string imageUrl);

        private void TtShare(string roomId, string title, string imageUrl)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            tt_share_app_message(roomId, title ?? "来一起玩吧！", imageUrl ?? "");
#endif
        }

        #endregion

        #region 抖音特有功能

        /// <summary>显示视频广告（抖音特有）</summary>
        public void ShowVideoAd(Action onRewarded, Action onFailed = null)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            TtShowVideoAd(onRewarded, onFailed);
#else
            Debug.Log("[DouyinLogin] 模拟显示视频广告");
            onRewarded?.Invoke();
#endif
        }

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void tt_show_video_ad();

        private void TtShowVideoAd(Action onRewarded, Action onFailed)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            tt_show_video_ad();
#endif
        }

        /// <summary>视频广告奖励回调</summary>
        public void OnVideoAdRewarded(string data)
        {
            Debug.Log("[DouyinLogin] 视频广告奖励");
            // 触发奖励事件
        }

        #endregion

        #region Mock Login (Editor)

        private IEnumerator MockLogin()
        {
            yield return new WaitForSeconds(0.5f);

            string mockOpenId = $"douyin_dev_{UnityEngine.Random.Range(1000, 9999)}";
            string mockToken = $"mock_token_{mockOpenId}";

            Token = mockToken;
            OpenId = mockOpenId;
            IsLoggedIn = true;
            loginInProgress = false;

            Debug.Log($"[DouyinLogin] 模拟登录成功: {mockOpenId}");
            OnLoginSuccess?.Invoke(mockOpenId);
        }

        #endregion
    }
}
