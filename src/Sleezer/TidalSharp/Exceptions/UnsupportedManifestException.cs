namespace TidalSharp.Exceptions;

public class UnsupportedManifestException : Exception
{
    public UnsupportedManifestException() { }
    public UnsupportedManifestException(string message) : base(message) { }
    public UnsupportedManifestException(string message, Exception inner) : base(message, inner) { }
}