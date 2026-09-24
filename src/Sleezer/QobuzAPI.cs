using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using NLog;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Indexers.Qobuz;
using QobuzApiSharp.Exceptions;
using QobuzApiSharp.Models.User;
using QobuzApiSharp.Service;

namespace NzbDrone.Plugin.Sleezer.Qobuz
{
    public class QobuzAPI
    {
        public static QobuzAPI? Instance { get; private set; }

        private readonly Logger _logger;
        private readonly string _configuredAppId;
        private readonly string _configuredAppSecret;
        private readonly string _account;
        private QobuzApiService _client;
        private Login? _login;

        private QobuzAPI(string? appId, string? appSecret, string account, Logger logger)
        {
            _logger = logger;
            _configuredAppId = appId ?? string.Empty;
            _configuredAppSecret = appSecret ?? string.Empty;
            _account = account;
            _client = CreateClient(appId, appSecret);
        }

        private static readonly object SignInLock = new();
        private static readonly TimeSpan ValidationInterval = TimeSpan.FromMinutes(5);
        private static DateTime _lastValidated = DateTime.MinValue;
        private static DateTime _lastBundleRefresh = DateTime.MinValue;

        /// <summary>The signed-in singleton for these settings: rebuilt when the app credentials or the account change, re-signed when the session stops working.</summary>
        // One locked path for every search: an unlocked rebuild let a concurrent search see the
        // half-built instance, find no Login and fail, which put the indexer into backoff.
        public static QobuzAPI EnsureSignedIn(QobuzIndexerSettings settings, Logger logger)
        {
            lock (SignInLock)
            {
                string account = FingerprintOf(settings);

                // Compared as configured, never against what QobuzApiService resolved: a blank
                // setting never equals a resolved one, so every search would re-authenticate.
                // Another account gets its own instance, since a search in flight reads its token lazily.
                if (Instance == null
                    || Instance.ConfiguredAppId != (settings.AppID ?? string.Empty)
                    || Instance.ConfiguredAppSecret != (settings.AppSecret ?? string.Empty)
                    || Instance._account != account)
                    Replace(settings, account, logger);

                if (Instance!.SessionIsValid())
                    return Instance;

                if (Instance.SignIn(settings) && Instance.SecretChecksOut())
                    return Instance;

                // Qobuz rotates the web player's app secret and bundle.js is cached for the process,
                // so a retry on the same client reuses the stale secret. At most once per interval.
                if (DateTime.UtcNow - _lastBundleRefresh > ValidationInterval)
                {
                    _lastBundleRefresh = DateTime.UtcNow;
                    QobuzApiHelper.ForgetBundle();
                    Replace(settings, account, logger);

                    if (Instance!.SignIn(settings) && Instance.SecretChecksOut())
                        return Instance;
                }

                // Search needs only the login; the secret signs download URLs.
                if (Instance!.Login != null)
                {
                    logger.Warn("Qobuz signed in, but the app secret was rejected: searches work, downloads will fail until it validates");
                    return Instance;
                }

                throw new ApiKeyException("Qobuz sign-in failed. Check the User ID and Auth Token, or the Email and Password, in the indexer settings, and the App ID and Secret if you set them.");
            }
        }

        // Must not dispose the outgoing client: a download in flight still holds it.
        private static void Replace(QobuzIndexerSettings settings, string account, Logger logger)
        {
            Instance = new QobuzAPI(settings.AppID, settings.AppSecret, account, logger);
            _lastValidated = DateTime.MinValue;
        }

        // Stamps the session valid only once the secret is proven; a transient failure is not proof.
        private bool SecretChecksOut()
        {
            try
            {
                if (!_client.IsAppSecretValid())
                    return false;
            }
            catch (Exception ex)
            {
                _logger.Debug("Qobuz app secret check failed with {ExceptionType}; re-checked on the next search", ex.GetType().Name);
                return true;
            }

            _lastValidated = DateTime.UtcNow;
            return true;
        }

        /// <summary>True while the session checked out within the last five minutes; re-checks the app secret after that.</summary>
        private bool SessionIsValid()
        {
            lock (SignInLock)
            {
                if (_login == null)
                    return false;

                if (DateTime.UtcNow - _lastValidated <= ValidationInterval)
                    return true;

                try
                {
                    if (!_client.IsAppSecretValid())
                        return false;
                }
                catch (Exception ex)
                {
                    // A transient failure is not a rejected secret; the next call re-checks.
                    _logger.Debug("Qobuz session check failed with {ExceptionType}; keeping the session", ex.GetType().Name);
                    return true;
                }

                _lastValidated = DateTime.UtcNow;
                return true;
            }
        }

        public QobuzApiService Client => _client;

        public Login? Login => _login;

        /// <summary>The signed-in session's token; EnsureSignedIn guarantees one before a request is built.</summary>
        public string AuthToken => _login?.AuthToken ?? throw new ApiKeyException("Qobuz session is not signed in.");

        /// <summary>The App ID/Secret this client was constructed with — blank when auto-detected.</summary>
        public string ConfiguredAppId => _configuredAppId;

        public string ConfiguredAppSecret => _configuredAppSecret;


        public static string FingerprintOf(QobuzIndexerSettings settings)
        {
            // Hashed rather than held: this is only ever compared for equality, so there
            // is no reason to keep the credentials themselves resident.
            var joined = string.Join('\u001f', settings.AppID, settings.AppSecret, settings.Email, settings.MD5Password, settings.UserID, settings.UserAuthToken);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined)));
        }

        /// <summary>Two-letter country of the signed-in account, or empty when not signed in.</summary>
        public string CountryCode => _login?.User?.CountryCode ?? string.Empty;

        public bool SignIn(QobuzIndexerSettings settings)
        {
            lock (SignInLock)
                return SignInCore(settings);
        }

        private bool SignInCore(QobuzIndexerSettings settings)
        {
            bool hasEmailPassword = !string.IsNullOrEmpty(settings.Email) && !string.IsNullOrEmpty(settings.MD5Password);
            bool hasToken = !string.IsNullOrEmpty(settings.UserID) && !string.IsNullOrEmpty(settings.UserAuthToken);

            if (!hasEmailPassword && !hasToken)
            {
                _logger.Debug("Qobuz sign-in skipped — no credentials configured");
                return false;
            }

            try
            {
                // Token login is preferred: an email/password session cannot call
                // getFileUrl, so downloads only work on the token path.
                _login = hasToken
                    ? _client.LoginWithToken(settings.UserID, settings.UserAuthToken)
                    : _client.LoginWithEmail(settings.Email, settings.MD5Password);

                _logger.Info("Qobuz signed in — user {UserId} country {Country} appId {AppId}",
                    _login?.User?.Id, CountryCode, _client.AppId);
                return true;
            }
            catch (ApiErrorResponseException ex)
            {
                _login = null;
                // Deliberately not passing `ex`: QobuzApiSharp embeds the auth token in
                // this exception's Message, and the parse variant carries the raw login
                // response. Only the sanitized status fields are safe to record.
                _logger.Error("Qobuz login rejected — status {Status} {StatusCode}, reason {Reason}",
                    ex.ResponseStatus, ex.ResponseStatusCode, ex.ResponseReason);
                return false;
            }
            catch (Exception ex)
            {
                _login = null;
                // Same reason as above — the message may quote the credential back.
                _logger.Error("Qobuz login failed: {ExceptionType}", ex.GetType().Name);
                return false;
            }
        }

        public string GetAPIUrl(string method, Dictionary<string, string>? parameters = null)
        {
            parameters ??= [];

            StringBuilder stringBuilder = new("https://www.qobuz.com/api.json/0.2");
            stringBuilder.Append(method);
            for (var i = 0; i < parameters.Count; i++)
            {
                var start = i == 0 ? "?" : "&";
                var key = WebUtility.UrlEncode(parameters.ElementAt(i).Key);
                var value = WebUtility.UrlEncode(parameters.ElementAt(i).Value);
                stringBuilder.Append(start + key + "=" + value);
            }

            return stringBuilder.ToString();
        }


        // An empty appId/appSecret makes QobuzApiService scrape both from the
        // web player's bundle.js, which is what we want by default — Qobuz
        // rotates them and the settings fields are only an override.
        private static QobuzApiService CreateClient(string? appId, string? appSecret)
            => !string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(appSecret)
                ? new QobuzApiService(appId, appSecret)
                : new QobuzApiService();
    }

    public enum AudioQuality
    {
        MP3320 = 5,
        FLACLossless = 6,
        FLACHiRes24Bit96kHz = 7,
        FLACHiRes24Bit192Khz = 27,
    }
}
