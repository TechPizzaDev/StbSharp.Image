using System;
using System.Runtime.Serialization;

namespace StbSharp
{
    public static partial class ImageRead
    {
        [Serializable]
        public class StbImageReadException : StbException
        {
            public ErrorCode ErrorCode { get; }

            public StbImageReadException()
            {
            }

            public StbImageReadException(ErrorCode errorCode) : this(errorCode, null)
            {
            }

            public StbImageReadException(ErrorCode errorCode, Exception? innerException) : this(errorCode.ToString(), innerException)
            {
                ErrorCode = errorCode;
            }

            public StbImageReadException(Exception? innerException) : this(null, innerException)
            {
            }

            public StbImageReadException(string? message) : base(message)
            {
            }

            public StbImageReadException(string? message, Exception? innerException) : base(message, innerException)
            {
            }

            protected StbImageReadException(SerializationInfo info, StreamingContext context) : base(info, context)
            {
            }
        }
    }
}