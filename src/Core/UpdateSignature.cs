using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CTD.Core
{
    /// <summary>
    /// 更新清单签名校验（ECDSA P-256 / SHA-256）。
    /// 为什么不是 Ed25519：.NET Framework 4.8 没有内置 Ed25519，引入第三方库会让
    /// 发射包多一个依赖；ECDSA P-256 由系统 CNG 提供，够用且无额外 DLL。
    /// 签名只覆盖会决定“装什么”的四个字段：version / url / sha256 / size，
    /// notes 只是展示文本，不参与签名。
    /// </summary>
    public static class UpdateSignature
    {
        public const string KeyId = "ctd-2026";

        // 公钥（对应 _keys\manifest-key.json；轮换时改这里并同时保留旧 keyid）
        private const string PubX = "VHvw1fKFsII9drCQ5k/tx1nJYZd/sb2PBNT8KPz77Xc=";
        private const string PubY = "+FT/Gx4G97NlsV/CLF+ph0dW42Hk6H7seRrtOZssy+U=";

        public static string Canonical(string version, string url, string sha256, long size)
        {
            return (version ?? "") + "\n" +
                   (url ?? "") + "\n" +
                   (sha256 ?? "").ToLowerInvariant() + "\n" +
                   size.ToString(CultureInfo.InvariantCulture);
        }

        public static bool Verify(string version, string url, string sha256, long size,
                                  string keyId, string sigBase64)
        {
            if (string.IsNullOrWhiteSpace(sigBase64)) return false;
            if (!string.Equals(keyId, KeyId, StringComparison.Ordinal)) return false;
            try
            {
                using (var ec = new ECDsaCng())
                {
                    var p = new ECParameters
                    {
                        Curve = ECCurve.NamedCurves.nistP256,
                        Q = new ECPoint
                        {
                            X = Convert.FromBase64String(PubX),
                            Y = Convert.FromBase64String(PubY)
                        }
                    };
                    ec.ImportParameters(p);
                    byte[] data = Encoding.UTF8.GetBytes(Canonical(version, url, sha256, size));
                    byte[] sig = Convert.FromBase64String(sigBase64);
                    return ec.VerifyData(data, sig, HashAlgorithmName.SHA256);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
