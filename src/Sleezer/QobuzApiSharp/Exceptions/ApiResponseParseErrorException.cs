// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable

// Vendored: overrides Exception.GetObjectData, obsolete since net8 but harmless here.
#pragma warning disable CS0672, SYSLIB0051
using System;
using System.Net;
using System.Runtime.Serialization;

namespace QobuzApiSharp.Exceptions
{
    [Serializable]
    public class ApiResponseParseErrorException : Exception
    {
        public string ResponseContent { get; }

        // Set when the unreadable body came with a failure status, so a caller can still tell a 401 from a 502.
        public HttpStatusCode? StatusCode { get; init; }

        public ApiResponseParseErrorException()
        {
        }

        public ApiResponseParseErrorException(string message) : base(message)
        {
        }

        public ApiResponseParseErrorException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public ApiResponseParseErrorException(string message, string requestContent)
            : base(message)
        {
            ResponseContent = requestContent;
        }

        public ApiResponseParseErrorException(string message, string requestContent, Exception innerException)
            : base(message, innerException)
        {
            ResponseContent = requestContent;
        }

        protected ApiResponseParseErrorException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            ResponseContent = info.GetString("ResponseContent");
            StatusCode = (HttpStatusCode?)(int?)info.GetValue("StatusCode", typeof(int?));
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue("ResponseContent", ResponseContent);
            info.AddValue("StatusCode", (int?)StatusCode, typeof(int?));
        }
    }
}