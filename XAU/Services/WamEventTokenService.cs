using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Security.Authentication.Web.Core;
using Windows.Security.Credentials;

namespace XAU.Services
{
    public static class WamEventTokenService
    {
        private const string MsaProvider = "https://login.live.com";
        private const string MsaAuthority = "consumers";
        private const string ClientId = "000000004424da1f"; // client id do app do Xbox
        private const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";
        private const string DeviceUrl = "https://device.auth.xboxlive.com/device/authenticate";
        private const string DeviceRp = "http://auth.xboxlive.com";
        private const string UserUrl = "https://user.auth.xboxlive.com/user/authenticate";
        private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
        private const string EventsRp = "http://events.xboxlive.com";

        private static readonly HttpClient Http = new HttpClient();

        public static async Task<List<WebAccount>> GetAccountsAsync()
        {
            var provider = await WebAuthenticationCoreManager.FindAccountProviderAsync(MsaProvider, MsaAuthority);
            if (provider == null)
                return new List<WebAccount>();
            var find = await WebAuthenticationCoreManager.FindAllAccountsAsync(provider, ClientId);
            if (find?.Accounts == null)
                return new List<WebAccount>();
            return find.Accounts.ToList();
        }

        public static async Task<string?> GetEventsTokenAsync(WebAccount account)
        {
            var provider = await WebAuthenticationCoreManager.FindAccountProviderAsync(MsaProvider, MsaAuthority);
            if (provider == null)
                return null;
            var request = new WebTokenRequest(provider, Scope, ClientId);
            var result = await WebAuthenticationCoreManager.GetTokenSilentlyAsync(request, account);
            string? msaToken = null;
            foreach (var rd in result.ResponseData)
                if (!string.IsNullOrEmpty(rd.Token)) { msaToken = rd.Token; break; }
            if (msaToken == null)
                return null;

            Http.DefaultRequestHeaders.Clear();
            Http.DefaultRequestHeaders.Add("x-xbl-contract-version", "2");
            Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var pop = new PopCryptoProvider();
            var deviceToken = await GetDeviceTokenAsync(msaToken, pop);
            if (deviceToken == null)
                return null;

            var (userToken, uhs) = await GetUserTokenAsync(msaToken);
            if (userToken == null)
                return null;

            var eventsXsts = await GetXstsAsync(userToken, deviceToken, EventsRp);
            if (eventsXsts == null)
                return null;

            return $"x:XBL3.0 x={uhs};{eventsXsts}";
        }

        private static async Task<string?> GetDeviceTokenAsync(string msaToken, PopCryptoProvider pop)
        {
            var v = Environment.OSVersion.Version;
            string winVer = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            var body = JsonSerializer.Serialize(new
            {
                Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "t=" + msaToken, Version = winVer, ProofKey = pop.ProofKey },
                RelyingParty = DeviceRp,
                TokenType = "JWT"
            });
            var req = new HttpRequestMessage(HttpMethod.Post, DeviceUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Signature", pop.SignRequest("POST", DeviceUrl, "", body));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return null;
            var rb = await resp.Content.ReadAsStringAsync();
            return JsonDocument.Parse(rb).RootElement.GetProperty("Token").GetString();
        }

        private static async Task<(string? token, string uhs)> GetUserTokenAsync(string msaToken)
        {
            var body = JsonSerializer.Serialize(new
            {
                Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "t=" + msaToken },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT"
            });
            var (code, resp) = await PostAsync(UserUrl, body);
            if (code != 200)
                return (null, "");
            var d = JsonDocument.Parse(resp).RootElement;
            return (d.GetProperty("Token").GetString(),
                d.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString() ?? "");
        }

        private static async Task<string?> GetXstsAsync(string userToken, string deviceToken, string rp)
        {
            var body = JsonSerializer.Serialize(new
            {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { userToken }, DeviceToken = deviceToken },
                RelyingParty = rp,
                TokenType = "JWT"
            });
            var (code, resp) = await PostAsync(XstsUrl, body);
            if (code != 200)
                return null;
            return JsonDocument.Parse(resp).RootElement.GetProperty("Token").GetString();
        }

        private static async Task<(int code, string body)> PostAsync(string url, string json)
        {
            using var resp = await Http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }
    }

    internal sealed class PopCryptoProvider
    {
        private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private object? _proofKey;
        public object ProofKey => _proofKey ??= BuildProofKey();

        private object BuildProofKey()
        {
            var p = _signer.ExportParameters(false);
            return new { kty = "EC", crv = "P-256", alg = "ES256", use = "sig", x = B64Url(p.Q.X!), y = B64Url(p.Q.Y!) };
        }

        public string SignRequest(string method, string reqUri, string token, string body)
        {
            var winTs = ((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 11644473600ul) * 10000000ul;
            var pathQuery = new Uri(reqUri).PathAndQuery;
            var strs = Encoding.ASCII.GetBytes($"{method}\0{pathQuery}\0{token}\0{body}\0");
            var payload = new byte[4 + 1 + 8 + 1 + strs.Length];
            BeInt(1).CopyTo(payload, 0); payload[4] = 0; BeULong(winTs).CopyTo(payload, 5); payload[13] = 0; strs.CopyTo(payload, 14);
            var sig = _signer.SignData(payload, HashAlgorithmName.SHA256);
            var header = new byte[12 + sig.Length];
            BeInt(1).CopyTo(header, 0); BeULong(winTs).CopyTo(header, 4); sig.CopyTo(header, 12);
            return Convert.ToBase64String(header);
        }

        private static byte[] BeInt(int v) { var b = BitConverter.GetBytes(v); if (BitConverter.IsLittleEndian) Array.Reverse(b); return b; }
        private static byte[] BeULong(ulong v) { var b = BitConverter.GetBytes(v); if (BitConverter.IsLittleEndian) Array.Reverse(b); return b; }
        private static string B64Url(byte[] d) => Convert.ToBase64String(d).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
