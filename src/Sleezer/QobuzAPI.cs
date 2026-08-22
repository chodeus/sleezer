using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using NLog;
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
        private QobuzApiService _client;
        private Login? _login;
        private string _credentialFingerprint = string.Empty;

        private QobuzAPI(string? appId, string? appSecret, Logger logger)
        {
            _logger = logger;
            _configuredAppId = appId ?? string.Empty;
            _configuredAppSecret = appSecret ?? string.Empty;
            _client = CreateClient(appId, appSecret);
        }

        /// <summary>Creates the singleton, or replaces it when credentials changed.</summary>
        public static void Initialize(string? appId, string? appSecret, Logger logger, bool forceRecreate = false)
        {
            if (Instance != null && !forceRecreate)
                return;

            // Must not dispose the outgoing client: a download in flight still holds it.
            Instance = new QobuzAPI(appId, appSecret, logger);
        }

        public QobuzApiService Client => _client;

        public Login? Login => _login;

        /// <summary>The App ID/Secret this client was constructed with — blank when auto-detected.</summary>
        public string ConfiguredAppId => _configuredAppId;

        public string ConfiguredAppSecret => _configuredAppSecret;

        /// <summary>
        /// The settings the live session was signed in with. Compared against current
        /// settings to decide whether to re-authenticate; comparing settings against
        /// runtime-resolved values instead would never match, because blank App ID and
        /// blank Email resolve to real values the settings do not carry.
        /// </summary>
        public string CredentialFingerprint => _credentialFingerprint;

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

                _credentialFingerprint = FingerprintOf(settings);
                _logger.Info("Qobuz signed in — user {UserId} country {Country} appId {AppId}",
                    _login?.User?.Id, CountryCode, _client.AppId);
                return true;
            }
            catch (ApiErrorResponseException ex)
            {
                _login = null;
                _credentialFingerprint = string.Empty;
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
                _credentialFingerprint = string.Empty;
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
