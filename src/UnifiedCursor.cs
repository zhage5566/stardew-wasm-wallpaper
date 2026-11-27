using System;
using System.IO;
using System.Security.Cryptography;

internal static class UnifiedCursor
{
    internal const string Resource = "_framework/dotnet.native.0zuq1uewq8.wasm0";
    private const string OriginalSha256 = "548790469707875f1eeb6396d22b191eb4ef87d18430a63c02c976586f9b22dd";

    internal static Stream Open(string path)
    {
        byte[] body = File.ReadAllBytes(path);
        using (var sha = SHA256.Create())
        {
            string hash = BitConverter.ToString(sha.ComputeHash(body)).Replace("-", "");
            if (!String.Equals(hash, OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The bundled WASM cursor code does not match the supported build.");
        }

        // The AOT getter selects the native pointer during gameplay. Its menu
        // counterpart is cursor-only and also has an inlined copy of the getter.
        ReplaceFunction(body, 6517142, 152, 0x41, 0x01);
        ReplaceFunction(body, 11820752, 702);

        // Inlined hardwareCursor reads in Darts, Slots and ChatBox. Replace the
        // unsigned field load with drop, i32.const 1, nop (same stack and size).
        foreach (int position in new[] { 11448038, 11758844, 12235988 })
        {
            body[position] = 0x1a;
            body[position + 1] = 0x41;
            body[position + 2] = 0x01;
            body[position + 3] = 0x01;
        }
        Launcher.Log("Unified cursor patch applied to the verified WASM runtime.");
        return new MemoryStream(body, false);
    }

    private static void ReplaceFunction(byte[] module, int start, int size, params byte[] result)
    {
        module[start] = 0; // No local declarations.
        Array.Copy(result, 0, module, start + 1, result.Length);
        for (int i = start + 1 + result.Length; i < start + size - 1; i++) module[i] = 0x01;
        module[start + size - 1] = 0x0b;
    }
}
