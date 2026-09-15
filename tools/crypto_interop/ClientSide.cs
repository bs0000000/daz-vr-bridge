// The other half of the interop harness: runs the client's BridgeCrypto against the
// same published vectors, then against the plugin's own build in both directions.
//
// Compiled against the real Assets/DazVrBridge/BridgeCrypto.cs -- with a couple of
// tiny stand-ins for the Unity types it names -- so what is tested is the file that
// ships, not a copy of it.

using System;
using System.Diagnostics;
using System.Text;
using DazVrBridge;

static class Interop
{
    static int _failures;

    static void Check(string what, string got, string want)
    {
        var ok = string.Equals(got, want, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
        if (!ok)
        {
            Console.WriteLine($"       got  {got}");
            Console.WriteLine($"       want {want}");
            _failures++;
        }
    }

    static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();

    static byte[] Unhex(string s)
    {
        var b = new byte[s.Length / 2];
        for (var i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    static string Run(string exe, string args, string stdin = null)
    {
        var info = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
        };
        var process = Process.Start(info);
        if (stdin != null) { process.StandardInput.WriteLine(stdin); process.StandardInput.Flush(); }
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    static string Field(string output, string name)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(name + " ", StringComparison.Ordinal))
                return line.Substring(name.Length + 1).Trim();
        }
        throw new Exception($"the plugin side printed no '{name}':\n{output}");
    }

    static int Main(string[] args)
    {
        var exe = args.Length > 0 ? args[0] : "plugin_side.exe";

        // --- 1. published vectors, so this proves correctness and not just agreement

        Check("sha256(\"abc\") - FIPS 180-4",
            Hex(BridgeCrypto.Sha256(Encoding.UTF8.GetBytes("abc"))),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

        Check("hmac-sha256 - RFC 4231 case 2",
            Hex(BridgeCrypto.HmacSha256(Encoding.UTF8.GetBytes("Jefe"),
                Encoding.UTF8.GetBytes("what do ya want for nothing?"))),
            "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843");

        var ikm = new byte[22];
        for (var i = 0; i < ikm.Length; i++) ikm[i] = 0x0b;
        Check("hkdf-sha256 - RFC 5869 case 1",
            Hex(BridgeCrypto.Hkdf(ikm, Unhex("000102030405060708090a0b0c"), Unhex("f0f1f2f3f4f5f6f7f8f9"), 42)),
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865");

        // --- 2. the same calls on the plugin's own build

        var plugin = Run(exe, "vectors");
        Check("sha256 matches the plugin",
            Hex(BridgeCrypto.Sha256(Encoding.UTF8.GetBytes("abc"))), Field(plugin, "sha256"));
        Check("hmac matches the plugin",
            Hex(BridgeCrypto.HmacSha256(Encoding.UTF8.GetBytes("Jefe"),
                Encoding.UTF8.GetBytes("what do ya want for nothing?"))), Field(plugin, "hmac"));
        Check("hkdf matches the plugin",
            Hex(BridgeCrypto.Hkdf(ikm, Unhex("000102030405060708090a0b0c"), Unhex("f0f1f2f3f4f5f6f7f8f9"), 42)),
            Field(plugin, "hkdf"));

        var key = Unhex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        var iv = Unhex("000102030405060708090a0b0c0d0e0f");
        Check("aes-256-cbc matches the plugin",
            Hex(BridgeCrypto.AesCbcEncrypt(key, iv, Encoding.UTF8.GetBytes("dazvrbridge interop"))),
            Field(plugin, "aescbc"));
        Check("base64url matches the plugin",
            BridgeCrypto.ToBase64Url(Unhex("fbff0110")), Field(plugin, "base64url"));

        // --- 3. records, both directions

        var premaster = Unhex("2b7e151628aed2a6abf7158809cf4f3c2b7e151628aed2a6abf7158809cf4f3c");
        var nonceC = Unhex("00112233445566778899aabbccddeeff");
        var nonceS = Unhex("ffeeddccbbaa99887766554433221100");
        var body = Encoding.UTF8.GetBytes("{\"t\":\"ping\",\"seq\":7}");

        // plugin seals (as server) -> client opens (as client)
        var record = Field(Run(exe, $"seal {Hex(premaster)} {Hex(nonceC)} {Hex(nonceS)} {Hex(body)}"), "record");
        var asClient = new SecureChannel();
        asClient.Arm(premaster, nonceC, nonceS, false);
        Check("the client opens the plugin's record", Hex(asClient.Open(Unhex(record))), Hex(body));

        // client seals (as client) -> plugin opens (as server)
        var sending = new SecureChannel();
        sending.Arm(premaster, nonceC, nonceS, false);
        var mine = sending.Seal(body);
        Check("the plugin opens the client's record",
            Field(Run(exe, $"open {Hex(premaster)} {Hex(nonceC)} {Hex(nonceS)} {Hex(mine)}"), "body"), Hex(body));

        // a tampered record must not open
        var tampered = (byte[])mine.Clone();
        tampered[tampered.Length - 40] ^= 0x01;
        var receiving = new SecureChannel();
        receiving.Arm(premaster, nonceC, nonceS, true);
        var refused = false;
        try { receiving.Open(tampered); } catch (Exception) { refused = true; }
        Check("a flipped bit is refused", refused.ToString(), "True");

        // a replayed record must not open twice
        var twice = new SecureChannel();
        twice.Arm(premaster, nonceC, nonceS, true);
        twice.Open(mine);
        var replayRefused = false;
        try { twice.Open(mine); } catch (Exception) { replayRefused = true; }
        Check("a replayed record is refused", replayRefused.ToString(), "True");

        // --- 4. the handshake's RSA leg, client encrypting to the plugin's real key

        var rsa = new Process
        {
            StartInfo = new ProcessStartInfo(exe, "rsa")
            {
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            }
        };
        rsa.Start();
        var modulus = Unhex(rsa.StandardOutput.ReadLine().Trim().Substring(2));
        var exponent = Unhex(rsa.StandardOutput.ReadLine().Trim().Substring(2));
        var secret = BridgeCrypto.Random(32);
        rsa.StandardInput.WriteLine(Hex(BridgeCrypto.RsaOaepEncrypt(modulus, exponent, secret)));
        rsa.StandardInput.Flush();
        var back = rsa.StandardOutput.ReadLine().Trim();
        rsa.WaitForExit();
        Check("the plugin decrypts what the client sealed to its key",
            back.StartsWith("plain ") ? back.Substring(6) : back, Hex(secret));

        Console.WriteLine(_failures == 0 ? "\nCRYPTO OK: both ends agree" : $"\n{_failures} FAILED");
        return _failures == 0 ? 1 - 1 : 1;
    }
}
