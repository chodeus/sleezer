// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable

// Vendored: formatter-based serialization, obsolete on net8 but harmless here.
#pragma warning disable SYSLIB0051
using System;
using System.Runtime.Serialization;

namespace QobuzApiSharp.Exceptions
{
    [Serializable]
    public class QobuzApiInitializationException : Exception
    {
        public QobuzApiInitializationException() { }

        public QobuzApiInitializationException(string message) : base(message) { }

        public QobuzApiInitializationException(string message, Exception innerException) : base(message, innerException) { }

        protected QobuzApiInitializationException(SerializationInfo info, StreamingContext context) : base(info, context) { }
    }
}