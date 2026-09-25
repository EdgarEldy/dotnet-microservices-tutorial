using System.Buffers.Text;
using System.Text;

namespace Identity.API.Security;

/// <summary>
/// Identity's confirmation and reset tokens contain '+', '/' and '='. They leave the service
/// Base64Url-encoded, so notification-worker can put them in a link without any escaping, and
/// come back in that form.
/// </summary>
public static class TokenEncoding
{
    public static string Encode(string token) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token));

    public static bool TryDecode(string encodedToken, out string token)
    {
        token = string.Empty;

        if (string.IsNullOrEmpty(encodedToken) || !Base64Url.IsValid(encodedToken))
        {
            return false;
        }

        token = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(encodedToken));
        return true;
    }
}
