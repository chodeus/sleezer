namespace NzbDrone.Plugin.Sleezer.Core.Model
{
    /// <summary>
    /// Exceptions specific to the YouTube trusted session authentication process
    /// </summary>
    public class TrustedSessionException : Exception
    {
        public TrustedSessionException(string message) : base(message)
        {
        }

        public TrustedSessionException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public TrustedSessionException()
        { }
    }
}