using QobuzApiSharp.Models.User;

namespace NzbDrone.Plugin.Sleezer.Core.Qobuz
{
    /// <summary>Why a signed-in Qobuz account cannot stream, or null when it can.</summary>
    public static class QobuzAccountCheck
    {
        // Without streaming rights Qobuz still signs in, then serves every track as a 30-second sample.
        public static string? StreamingProblem(Login? login, DateTimeOffset now)
        {
            User? user = login?.User;
            if (user == null)
                return null;

            Parameters? rights = user.Credential?.Parameters;
            if (rights?.LosslessStreaming == true || rights?.LossyStreaming == true)
                return null;

            string plan = string.IsNullOrWhiteSpace(user.Credential?.Label) ? "no plan" : user.Credential!.Label;
            string problem = $"Qobuz account {user.Id} has no streaming rights ({plan})";

            Subscription? subscription = user.Subscription;
            if (subscription?.EndDate is DateTimeOffset ended && ended <= now)
                problem += $"; its {subscription.Offer} subscription ended {ended:yyyy-MM-dd}";

            return problem + ". Downloads will be 30-second samples until the account has an active subscription.";
        }
    }
}
