namespace TidalSharp.Exceptions;

public class UnavailableMediaException : Exception
{
    public UnavailableMediaException() { }
    public UnavailableMediaException(string message) : base(message) { }
    public UnavailableMediaException(string message, Exception inner) : base(message, inner) { }
}