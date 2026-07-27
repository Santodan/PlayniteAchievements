using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Steam
{
    internal enum SteamAsyncConfigResponseState
    {
        Success = 0,
        NeedsWarmup,
        HardFailure
    }

    internal sealed class SteamAsyncConfigParseResult
    {
        public SteamAsyncConfigResponseState State { get; private set; }

        public string Token { get; private set; }

        public string Detail { get; private set; }

        public Exception Exception { get; private set; }

        public bool HasToken =>
            State == SteamAsyncConfigResponseState.Success &&
            !string.IsNullOrWhiteSpace(Token);

        private SteamAsyncConfigParseResult()
        {
        }

        public static SteamAsyncConfigParseResult Success(string token)
        {
            return new SteamAsyncConfigParseResult
            {
                State = SteamAsyncConfigResponseState.Success,
                Token = token?.Trim(),
                Detail = "success"
            };
        }

        public static SteamAsyncConfigParseResult NeedsWarmup(string detail)
        {
            return new SteamAsyncConfigParseResult
            {
                State = SteamAsyncConfigResponseState.NeedsWarmup,
                Detail = detail ?? "needs_warmup"
            };
        }

        public static SteamAsyncConfigParseResult HardFailure(string detail, Exception exception = null)
        {
            return new SteamAsyncConfigParseResult
            {
                State = SteamAsyncConfigResponseState.HardFailure,
                Detail = detail ?? "hard_failure",
                Exception = exception
            };
        }
    }

    internal sealed class SteamAsyncConfigRequestResult
    {
        public bool IsSuccessStatusCode { get; private set; }

        public int StatusCode { get; private set; }

        public string ReasonPhrase { get; private set; }

        public string Content { get; private set; }

        public Exception Exception { get; private set; }

        private SteamAsyncConfigRequestResult()
        {
        }

        public static SteamAsyncConfigRequestResult Success(int statusCode, string reasonPhrase, string content)
        {
            return new SteamAsyncConfigRequestResult
            {
                IsSuccessStatusCode = true,
                StatusCode = statusCode,
                ReasonPhrase = reasonPhrase,
                Content = content
            };
        }

        public static SteamAsyncConfigRequestResult HttpFailure(int statusCode, string reasonPhrase, string content = null)
        {
            return new SteamAsyncConfigRequestResult
            {
                IsSuccessStatusCode = false,
                StatusCode = statusCode,
                ReasonPhrase = reasonPhrase,
                Content = content
            };
        }

        public static SteamAsyncConfigRequestResult RequestFailure(Exception exception)
        {
            return new SteamAsyncConfigRequestResult
            {
                IsSuccessStatusCode = false,
                Exception = exception
            };
        }
    }

    internal static class SteamAsyncConfigTokenHelper
    {
        public static SteamAsyncConfigParseResult ParseResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return SteamAsyncConfigParseResult.NeedsWarmup("empty_payload");
            }

            try
            {
                var root = JToken.Parse(json) as JObject;
                if (root == null)
                {
                    return SteamAsyncConfigParseResult.HardFailure("root_not_object");
                }

                var dataToken = root["data"];
                if (dataToken == null || dataToken.Type == JTokenType.Null || dataToken.Type == JTokenType.Undefined)
                {
                    return SteamAsyncConfigParseResult.NeedsWarmup("missing_data");
                }

                if (dataToken.Type == JTokenType.Array)
                {
                    return SteamAsyncConfigParseResult.NeedsWarmup("array_data");
                }

                if (dataToken.Type != JTokenType.Object)
                {
                    return SteamAsyncConfigParseResult.HardFailure($"unexpected_data_type_{dataToken.Type}");
                }

                var token = (dataToken["webapi_token"]?.Value<string>() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    return SteamAsyncConfigParseResult.NeedsWarmup("missing_token");
                }

                return SteamAsyncConfigParseResult.Success(token);
            }
            catch (JsonException ex)
            {
                return SteamAsyncConfigParseResult.HardFailure("invalid_json", ex);
            }
        }

        public static async Task<string> ResolveTokenAsync(
            Func<CancellationToken, Task<SteamAsyncConfigRequestResult>> requestAsync,
            Func<CancellationToken, Task<bool>> warmStoreAsync,
            Action syncCookiesAction,
            ILogger logger,
            CancellationToken ct)
        {
            if (requestAsync == null) throw new ArgumentNullException(nameof(requestAsync));

            var initialResult = await requestAsync(ct).ConfigureAwait(false);
            var initialParse = ParseRequestResult(initialResult, "Store token request", logger);
            if (initialParse.HasToken)
            {
                return initialParse.Token;
            }

            if (initialParse.State != SteamAsyncConfigResponseState.NeedsWarmup)
            {
                return null;
            }

            logger?.Info($"[SteamAch] Store token response requires store warmup ({initialParse.Detail}).");
            logger?.Info("[SteamAch] Warming Steam store session before retrying token request...");

            var warmupSucceeded = false;
            try
            {
                warmupSucceeded = warmStoreAsync != null && await warmStoreAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[SteamAch] Steam store warmup failed before token retry.");
            }

            try
            {
                syncCookiesAction?.Invoke();
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[SteamAch] Failed to sync Steam cookies after store warmup.");
            }

            var retryResult = await requestAsync(ct).ConfigureAwait(false);
            var retryParse = ParseRequestResult(retryResult, "Store token retry", logger);
            if (retryParse.HasToken)
            {
                logger?.Info("[SteamAch] Steam store warmup resolved token request.");
                return retryParse.Token;
            }

            var retryDetail = retryParse.Detail;
            if (string.IsNullOrWhiteSpace(retryDetail))
            {
                retryDetail = retryParse.State == SteamAsyncConfigResponseState.NeedsWarmup
                    ? "missing_token"
                    : "hard_failure";
            }

            logger?.Info($"[SteamAch] Steam store warmup retry did not return a usable token ({retryDetail}, warmupSucceeded={warmupSucceeded}).");
            return null;
        }

        private static SteamAsyncConfigParseResult ParseRequestResult(
            SteamAsyncConfigRequestResult requestResult,
            string operationLabel,
            ILogger logger)
        {
            if (requestResult == null)
            {
                logger?.Debug($"[SteamAch] {operationLabel} failed: no response.");
                return SteamAsyncConfigParseResult.HardFailure("no_response");
            }

            if (requestResult.Exception != null)
            {
                logger?.Debug(requestResult.Exception, $"[SteamAch] {operationLabel} failed.");
                return SteamAsyncConfigParseResult.HardFailure("request_exception", requestResult.Exception);
            }

            if (!requestResult.IsSuccessStatusCode)
            {
                logger?.Debug($"[SteamAch] {operationLabel} failed: {requestResult.StatusCode} {requestResult.ReasonPhrase}");
                return SteamAsyncConfigParseResult.HardFailure("http_failure");
            }

            var parseResult = ParseResponse(requestResult.Content);
            if (parseResult.State == SteamAsyncConfigResponseState.HardFailure)
            {
                if (parseResult.Exception != null)
                {
                    logger?.Debug(parseResult.Exception, $"[SteamAch] {operationLabel} returned malformed async config JSON.");
                }
                else
                {
                    logger?.Debug($"[SteamAch] {operationLabel} returned an unsupported async config payload ({parseResult.Detail}).");
                }
            }

            return parseResult;
        }
    }
}
