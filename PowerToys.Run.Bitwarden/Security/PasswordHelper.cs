using System.Runtime.InteropServices;
using System.Security;

namespace PowerToys.Run.Bitwarden.Security;

// SecureString isn't a security boundary on modern .NET; it just narrows the
// window during which the cleartext sits in process memory. We use it to carry
// the master password from the WPF PasswordBox to the call site that has to
// materialize a plain string (env var, JSON body), then zero+free the marshalled
// buffer immediately. The managed string copy still lives until GC, but only for
// the duration of the actual unlock call.
internal static class PasswordHelper
{
    public static T UseAsString<T>(SecureString secret, Func<string, T> use)
    {
        if (secret is null || secret.Length == 0) return use(string.Empty);
        var bstr = Marshal.SecureStringToBSTR(secret);
        try
        {
            var plain = Marshal.PtrToStringBSTR(bstr);
            return use(plain);
        }
        finally
        {
            Marshal.ZeroFreeBSTR(bstr);
        }
    }
}
