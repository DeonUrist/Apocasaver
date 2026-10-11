using System;
using System.IO;
using System.Reflection;

namespace Apocasaver
{
    internal static class SaveFailurePolicy
    {
        internal static Exception Unwrap(Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            return error;
        }

        // Serialization/layout/validation errors are diagnostics, not disk failures.
        // Actual ES3 disk operations also report errors directly at their IO boundary.
        internal static bool IsDiskFailure(Exception error)
        {
            error = Unwrap(error);
            return error is UnauthorizedAccessException ||
                error is IOException && !(error is InvalidDataException) && !(error is EndOfStreamException);
        }
    }
}
