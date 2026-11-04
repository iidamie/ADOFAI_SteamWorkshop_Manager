using System.Security.Cryptography;
using System.Text.Json;

namespace SteamWorkshopManager;

internal sealed class SteamAuthStore
{
    private const int MaximumFileBytes = 128 * 1024;
    private static readonly JsonSerializerOptions EnvelopeJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _filePath;

    public string DirectoryPath => _directory;
    public bool HasSavedSession => File.Exists(_filePath);

    public SteamAuthStore(string modDirectory)
    {
        _directory = Path.Combine(modDirectory, "data");
        _filePath = Path.Combine(_directory, "steam-session.json");
        Directory.CreateDirectory(_directory);
    }

    public SteamSavedLogin? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath)) return null;
            byte[] fileBytes;
            using (var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length is < 1 or > MaximumFileBytes) throw new InvalidDataException("已保存的 Steam 会话文件大小无效，请清除后重新登录。");
                fileBytes = new byte[checked((int)stream.Length)];
                try { stream.ReadExactly(fileBytes); }
                catch
                {
                    CryptographicOperations.ZeroMemory(fileBytes);
                    throw;
                }
            }
            try
            {
                using var document = JsonDocument.Parse(fileBytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw InvalidSession();
                if (root.TryGetProperty("format", out _))
                {
                    var envelope = JsonSerializer.Deserialize<SessionEnvelope>(fileBytes, EnvelopeJson);
                    if (envelope is not { Format: 1 } || envelope.Protection != AndroidKeystoreSessionCipher.Protection
                        || envelope.Nonce == null || envelope.Ciphertext == null) throw InvalidSession();
                    var plaintext = AndroidKeystoreSessionCipher.Open(envelope.Nonce, envelope.Ciphertext);
                    try { return ReadLogin(plaintext); }
                    finally { CryptographicOperations.ZeroMemory(plaintext); }
                }
                // Protect this Mod's 1.0.0 file in place, before using it to sign in.
                // No other Mod directories or alternate session paths are read.
                if (!root.TryGetProperty("RefreshToken", out _) || !root.TryGetProperty("AccountName", out _)
                    || !root.TryGetProperty("SteamId", out _)) throw InvalidSession();
                try
                {
                    var saved = ReadLogin(fileBytes);
                    Save(saved);
                    return saved;
                }
                catch (Exception exception)
                {
                    try { File.Delete(_filePath); }
                    catch (Exception deleteError)
                    {
                        throw new IOException("现有明文会话无法加密且未能删除，请手动删除 Mod 数据目录中的 steam-session.json 后重新登录。", deleteError);
                    }
                    throw new CryptographicException("现有会话无法加密，已删除明文文件，请重新登录；不会继续明文保存。", exception);
                }
            }
            catch (JsonException) { throw InvalidSession(); }
            finally { CryptographicOperations.ZeroMemory(fileBytes); }
        }
    }

    public void Save(SteamSavedLogin saved)
    {
        Validate(saved);
        lock (_gate)
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(saved);
            try
            {
                var sealedSession = AndroidKeystoreSessionCipher.Seal(plaintext);
                var envelope = new SessionEnvelope(1, AndroidKeystoreSessionCipher.Protection,
                    sealedSession.Nonce, sealedSession.Ciphertext);
                var encryptedJson = JsonSerializer.SerializeToUtf8Bytes(envelope, EnvelopeJson);
                Directory.CreateDirectory(_directory);
                var temporary = Path.Combine(_directory, $"steam-session.{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        RestrictPermissions(temporary);
                        stream.Write(encryptedJson);
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, _filePath, overwrite: true);
                    RestrictPermissions(_filePath);
                }
                finally { try { File.Delete(temporary); } catch { } }
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Exception? fileError = null;
            Exception? keyError = null;
            try { File.Delete(_filePath); } catch (Exception exception) { fileError = exception; }
            // Also destroy the key so previously copied ciphertext is no longer usable.
            try { AndroidKeystoreSessionCipher.DeleteKey(); } catch (Exception exception) { keyError = exception; }
            if (fileError != null)
                throw new IOException("未能删除会话文件，请手动清理 Mod 数据目录中的 steam-session.json。", fileError);
            if (keyError != null)
                throw new CryptographicException("会话文件已删除，但设备密钥未能清除，请稍后重试。", keyError);
        }
    }

    private static SteamSavedLogin ReadLogin(byte[] plaintext)
    {
        var saved = JsonSerializer.Deserialize<SteamSavedLogin>(plaintext) ?? throw InvalidSession();
        Validate(saved);
        return saved;
    }

    private static void Validate(SteamSavedLogin saved)
    {
        if (string.IsNullOrWhiteSpace(saved.AccountName) || saved.AccountName.Length > 128 || saved.SteamId == 0
            || string.IsNullOrWhiteSpace(saved.RefreshToken) || saved.RefreshToken.Length > 32768 || saved.GuardData?.Length > 32768)
            throw InvalidSession();
    }

    private static InvalidDataException InvalidSession() => new("已保存的 Steam 会话数据无效，请清除后重新登录。");

    private static void RestrictPermissions(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (PlatformNotSupportedException) { }
    }

    private sealed record SessionEnvelope(int Format, string Protection, byte[] Nonce, byte[] Ciphertext);
}
