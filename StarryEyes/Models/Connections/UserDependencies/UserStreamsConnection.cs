﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using StarryEyes.Breezy.Api.Streaming;
using StarryEyes.Breezy.Authorize;
using StarryEyes.Breezy.DataModel;
using StarryEyes.Models.Hubs;
using StarryEyes.Models.Stores;

namespace StarryEyes.Models.Connections.UserDependencies
{
    public sealed class UserStreamsConnection : ConnectionBase
    {
        public const int MaxTrackingKeywordBytes = 60;
        public const int MaxTrackingKeywordCounts = 100;

        private const int HardErrorRetryMaxCount = 3;
        private IDisposable _connection;

        private BackOffMode _currentBackOffMode = BackOffMode.None;
        private long _currentBackOffWaitCount;
        private int _hardErrorRetryCount;

        private string[] _trackKeywords;

        public UserStreamsConnection(AuthenticateInfo ai)
            : base(ai)
        {
        }

        public IEnumerable<string> TrackKeywords
        {
            get { return _trackKeywords; }
            set { _trackKeywords = value.ToArray(); }
        }

        /// <summary>
        ///     User Streams is connected
        /// </summary>
        public bool IsConnected
        {
            get { return _connection != null; }
        }

        public event Action<bool> IsConnectionAliveEvent;

        /// <summary>
        ///     Connect to user streams.
        ///     <para />
        ///     Or, update connected streams.
        /// </summary>
        public void Connect()
        {
            CheckDisposed();
            Disconnect();
            _connection = AuthInfo.ConnectToUserStreams(_trackKeywords)
                                  .Do(_ => _currentBackOffMode = BackOffMode.None) // initialize back-off
                                  .Subscribe(
                                      Register,
                                      HandleException,
                                      () =>
                                      {
                                          if (_connection != null)
                                          {
                                              // make reconnect.
                                              Disconnect();
                                              Debug.WriteLine("***Auto reconnect***");
                                              Connect();
                                          }
                                      });
            RaiseIsConnectedEvent(true);
        }

        /// <summary>
        ///     Disconnect from user streams.
        /// </summary>
        public void Disconnect()
        {
            if (_connection != null)
            {
                IDisposable disposal = _connection;
                _connection = null;
                disposal.Dispose();
                RaiseIsConnectedEvent(false);
            }
        }

        private void Register(TwitterStreamingElement elem)
        {
            _hardErrorRetryCount = 0; // initialize error count
            switch (elem.EventType)
            {
                case EventType.Empty:
                    // deliver tweet or something.
                    if (elem.Status != null)
                    {
                        StatusStore.Store(elem.Status);
                    }
                    if (elem.DeletedId != null)
                    {
                        StatusStore.Remove(elem.DeletedId.Value);
                    }
                    break;
                case EventType.Follow:
                case EventType.Unfollow:
                    long source = elem.EventSourceUser.Id;
                    long target = elem.EventTargetUser.Id;
                    bool isFollowed = elem.EventType == EventType.Follow;
                    if (source == AuthInfo.Id) // follow or remove
                    {
                        AuthInfo.GetAccountData().SetFollowing(target, isFollowed);
                    }
                    else if (target == AuthInfo.Id) // followed or removed
                    {
                        AuthInfo.GetAccountData().SetFollower(source, isFollowed);
                    }
                    else
                    {
                        return;
                    }
                    if (isFollowed)
                        RegisterEvent(elem);
                    break;
                case EventType.Blocked:
                    if (elem.EventSourceUser.Id != AuthInfo.Id) return;
                    AuthInfo.GetAccountData().AddBlocking(elem.EventTargetUser.Id);
                    break;
                case EventType.Unblocked:
                    if (elem.EventSourceUser.Id != AuthInfo.Id) return;
                    AuthInfo.GetAccountData().RemoveBlocking(elem.EventTargetUser.Id);
                    break;
                default:
                    RegisterEvent(elem);
                    break;
            }
        }

        private void RegisterEvent(TwitterStreamingElement elem)
        {
            switch (elem.EventType)
            {
                case EventType.Blocked:
                    EventHub.NotifyBlocked(elem.EventSourceUser, elem.EventTargetUser);
                    break;
                case EventType.Unblocked:
                    EventHub.NotifyUnblocked(elem.EventSourceUser, elem.EventTargetUser);
                    break;
                case EventType.Favorite:
                    EventHub.NotifyFavorited(elem.EventSourceUser, elem.EventTargetTweet);
                    break;
                case EventType.Unfavorite:
                    EventHub.NotifyUnfavorited(elem.EventSourceUser, elem.EventTargetTweet);
                    break;
                case EventType.Follow:
                    EventHub.NotifyFollowed(elem.EventSourceUser, elem.EventTargetUser);
                    break;
                case EventType.Unfollow:
                    EventHub.NotifyUnfollwed(elem.EventSourceUser, elem.EventTargetUser);
                    break;
                case EventType.LimitationInfo:
                    EventHub.NotifyLimitationInfoGot(AuthInfo, elem.TrackLimit.GetValueOrDefault());
                    break;
                default:
                    return;
            }
        }

        private void HandleException(Exception ex)
        {
            Disconnect();
            var wex = ex as WebException;
            if (wex != null)
            {
                if (wex.Status == WebExceptionStatus.ProtocolError)
                {
                    var res = wex.Response as HttpWebResponse;
                    if (res != null)
                    {
                        // protocol error
                        switch (res.StatusCode)
                        {
                            case HttpStatusCode.Unauthorized:
                                // ERR: Unauthorized, invalid OAuth request?
                                if (CheckHardError())
                                {
                                    RaiseDisconnectedByError(
                                        "ユーザー認証が行えません。",
                                        "PCの時刻設定が正しいか確認してください。回復しない場合は、OAuth認証を再度行ってください。");
                                    return;
                                }
                                break;
                            case HttpStatusCode.Forbidden:
                            case HttpStatusCode.NotFound:
                                if (CheckHardError())
                                {
                                    RaiseDisconnectedByError(
                                        "ユーザーストリーム接続が一時的、または恒久的に利用できなくなっています。",
                                        "エンドポイントへの接続時にアクセスが拒否されたか、またはエンドポイントが削除されています。");
                                    return;
                                }
                                break;
                            case HttpStatusCode.NotAcceptable:
                            case HttpStatusCode.RequestEntityTooLarge:
                                RaiseDisconnectedByError(
                                    "トラックしているキーワードが長すぎるか、不正な可能性があります。",
                                    "(トラック中のキーワード:" + _trackKeywords.JoinString(", ") + ")");
                                return;
                            case HttpStatusCode.RequestedRangeNotSatisfiable:
                                RaiseDisconnectedByError(
                                    "ユーザーストリームに接続できません。",
                                    "(システム エラー: 416 Range Unacceptable. Elevated permission is required or paramter is out of range.)");
                                return;
                            case (HttpStatusCode)420:
                                // ERR: Too many connections
                                // (other client is already connected?)
                                if (CheckHardError())
                                {
                                    RaiseDisconnectedByError(
                                        "ユーザーストリーム接続が制限されています。",
                                        "Krileが多重起動していないか確認してください。短時間に何度も接続を試みていた場合は、しばらく待つと再接続できるようになります。");
                                    return;
                                }
                                break;
                        }
                    }
                    // else -> backoff
                    if (_currentBackOffMode == BackOffMode.Protocol)
                        _currentBackOffWaitCount += _currentBackOffWaitCount; // wait count is raised exponentially.
                    else
                        _currentBackOffWaitCount = 5000;
                    if (_currentBackOffWaitCount >= 320000) // max wait is 320 sec.
                    {
                        RaiseDisconnectedByError(
                            "Twitterが不安定な状態になっています。",
                            "プロトコル エラーにより、ユーザーストリームに既定のリトライ回数内で接続できませんでした。");
                        return;
                    }
                }
                else
                {
                    // network error
                    // -> backoff
                    if (_currentBackOffMode == BackOffMode.Network)
                        _currentBackOffMode += 250; // wait count is raised linearly.
                    else
                        _currentBackOffWaitCount = 250; // wait starts 250ms
                    if (_currentBackOffWaitCount >= 16000) // max wait is 16 sec.
                    {
                        RaiseDisconnectedByError(
                            "Twitterが不安定な状態になっています。",
                            "ネットワーク エラーにより、ユーザーストリームに規定のリトライ回数内で接続できませんでした。");
                        return;
                    }
                }
            }
            else
            {
                _currentBackOffMode = BackOffMode.None;
                if (_currentBackOffWaitCount == 110)
                {
                    RaiseDisconnectedByError(
                        "ユーザーストリーム接続が何らかのエラーの頻発で停止しました。",
                        "Twitterが不安定な状態になっているか、仕様が変更された可能性があります: " + ex.Message);
                    return;
                }
                if (_currentBackOffWaitCount >= 108 && _currentBackOffWaitCount <= 109)
                {
                    _currentBackOffWaitCount++;
                }
                else
                {
                    _currentBackOffWaitCount = 108; // wait shortly
                }
            }
            Debug.WriteLine("*** USER STREAMS error ***" + Environment.NewLine + ex);
            Debug.WriteLine(" -> reconnect.");
            // parsing error, auto-reconnect
            Observable.Timer(TimeSpan.FromMilliseconds(_currentBackOffWaitCount))
                      .Subscribe(_ => Connect());
        }

        private bool CheckHardError()
        {
            _hardErrorRetryCount++;
            if (_hardErrorRetryCount > HardErrorRetryMaxCount)
                return true;
            return false;
        }

        private void RaiseIsConnectedEvent(bool connected)
        {
            Action<bool> handler = IsConnectionAliveEvent;
            if (handler != null)
                handler(connected);
        }

        private void RaiseDisconnectedByError(string header, string detail)
        {
            RaiseErrorNotification(
                "UserStreams_Reconnection_" + AuthInfo.UnreliableScreenName,
                header, detail,
                "再接続", () =>
                {
                    if (!IsDisposed)
                        Connect();
                });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Disconnect();
        }

        private enum BackOffMode
        {
            None,
            Network,
            Protocol,
        }
    }
}