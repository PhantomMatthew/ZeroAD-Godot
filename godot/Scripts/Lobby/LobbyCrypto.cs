using System;
using System.Security.Cryptography;
using System.Text;

namespace ZeroAD.Godot.Lobby;

/// <summary>大厅密码加密（逐字移植 source/lobby/scripting/JSInterface_Lobby.cpp:157-185）。
/// PBKDF2-SHA256，1337 迭代，salt = SHA256(固定 32 字节 ‖ username)，输出 hex 大写。
/// 必须逐字节复制才能与现有大厅服务器认证兼容。</summary>
public static class LobbyCrypto
{
    private static readonly byte[] SaltBase = new byte[32]
    {
        244, 243, 249, 244, 32, 33, 34, 35, 10, 11, 12, 13, 14, 15, 16, 17,
        18, 19, 20, 32, 33, 244, 224, 127, 129, 130, 140, 153, 133, 123, 234, 123
    };

    private const int Iterations = 1337;
    private const int DigestSize = 32;  // SHA-256 = 32 bytes

    /// <summary>加密密码。逐字移植 EncryptPassword。</summary>
    public static string EncryptPassword(string password, string username)
    {
        // salt = SHA256(salt_base ‖ username)
        using var sha = SHA256.Create();
        var saltInput = new byte[SaltBase.Length + Encoding.UTF8.GetByteCount(username)];
        Buffer.BlockCopy(SaltBase, 0, saltInput, 0, SaltBase.Length);
        Encoding.UTF8.GetBytes(username, 0, username.Length, saltInput, SaltBase.Length);
        var salt = sha.ComputeHash(saltInput);

        // PBKDF2-HMAC-SHA256(password, salt, iterations=1337, output=32 bytes)
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        var encrypted = pbkdf2.GetBytes(DigestSize);

        // 输出 hex 大写
        return Convert.ToHexString(encrypted);
    }
}
