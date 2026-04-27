using System;
using Newtonsoft.Json.Linq;

namespace TidalSharp;

// Centralised "is this Tidal response telling us our session is expired?"
// check. Two distinct paths need this: TidalSharp.API.Call (download path)
// and the indexer's search FetchPage override. Keeping the rules in one
// place stops the two callers from drifting.
public static class ExpiredTokenDetector
{
    // Tidal sometimes returns 401 with the literal "The token has expired."
    // userMessage. It also returns 401 with "countryCode parameter missing"
    // even when our request DID include countryCode — issue #42 in TrevTV's
    // upstream documents this as an expired-session sentinel.
    public static bool LooksExpired(string? responseBody, bool requestHadCountryCode)
    {
        if (string.IsNullOrEmpty(responseBody))
            return false;

        string? userMessage;
        try
        {
            userMessage = JObject.Parse(responseBody).GetValue("userMessage")?.ToString();
        }
        catch (Exception)
        {
            // Non-JSON 401 body — Tidal always returns JSON for these errors,
            // so anything else isn't the expired-session shape we recognize.
            return false;
        }

        if (string.IsNullOrEmpty(userMessage))
            return false;

        if (userMessage.Contains("The token has expired.", StringComparison.Ordinal))
            return true;

        // Only treat the missing-countryCode sentinel as expired when our
        // request DID include countryCode — otherwise it's a real client bug
        // we shouldn't paper over with a refresh.
        if (requestHadCountryCode &&
            userMessage.Contains("countryCode parameter missing", StringComparison.Ordinal))
            return true;

        return false;
    }
}
