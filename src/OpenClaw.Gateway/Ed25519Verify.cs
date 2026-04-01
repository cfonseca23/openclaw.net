using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace OpenClaw.Gateway;

/// <summary>
/// Ed25519 signature verification for Discord interaction webhooks.
/// Uses BouncyCastle so verification is correct and consistent across runtime targets.
/// </summary>
internal static class Ed25519Verify
{
    /// <summary>
    /// Verifies an Ed25519 signature.
    /// Returns false if verification fails.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message, ReadOnlySpan<byte> publicKey)
    {
        if (signature.Length != 64 || publicKey.Length != 32)
            return false;

        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(publicKey.ToArray()));
            var payload = message.ToArray();
            verifier.BlockUpdate(payload, 0, payload.Length);
            return verifier.VerifySignature(signature.ToArray());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if the runtime supports Ed25519 verification.
    /// </summary>
    public static bool IsSupported => true;
}
