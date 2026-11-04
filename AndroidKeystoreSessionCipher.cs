using System.Security.Cryptography;
using System.Text;
using static SteamWorkshopManager.AndroidJniScope.Value;

namespace SteamWorkshopManager;

internal static class AndroidKeystoreSessionCipher
{
    internal const string Protection = "AndroidKeyStore/AES-256-GCM";
    private const string Alias = "SteamWorkshopManager.SteamSession.AesGcm.v1";
    private const string Builder = "android/security/keystore/KeyGenParameterSpec$Builder";
    private const string BuilderResult = "Landroid/security/keystore/KeyGenParameterSpec$Builder;";
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("SteamWorkshopManager|steam-session|1|" + Protection);
    private static readonly object Gate = new();

    internal static SealedSession Seal(byte[] plaintext)
    {
        if (plaintext.Length is < 1 or > 65536) throw new CryptographicException("Steam 会话数据大小无效。");
        lock (Gate)
        {
            using var jni = CreateScope();
            var key = GetKey(jni, create: true);
            var cipher = jni.StaticObject("javax/crypto/Cipher", "getInstance", "(Ljava/lang/String;)Ljavax/crypto/Cipher;",
                Ref(jni.String("AES/GCM/NoPadding")));
            jni.Void(cipher, "init", "(ILjava/security/Key;)V", Int(1), Ref(key));
            var nonce = jni.ReadBytes(jni.Object(cipher, "getIV", "()[B"));
            if (nonce.Length != 12) throw new CryptographicException("设备密钥库返回了不支持的随机 IV。");
            jni.Void(cipher, "updateAAD", "([B)V", Ref(jni.Bytes(AssociatedData)));
            var input = jni.Bytes(plaintext);
            try
            {
                var encrypted = jni.ReadBytes(jni.Object(cipher, "doFinal", "([B)[B", Ref(input)));
                if (encrypted.Length != plaintext.Length + 16) throw new CryptographicException("设备会话加密结果无效。");
                return new SealedSession(nonce, encrypted);
            }
            finally { jni.ClearBytes(input); }
        }
    }

    internal static byte[] Open(byte[] nonce, byte[] ciphertext)
    {
        if (nonce.Length != 12 || ciphertext.Length is < 17 or > 65552)
            throw new CryptographicException("加密会话格式无效，请重新登录。");
        lock (Gate)
        {
            try
            {
                using var jni = CreateScope();
                var key = GetKey(jni, create: false);
                var parameters = jni.New("javax/crypto/spec/GCMParameterSpec", "(I[B)V", Int(128), Ref(jni.Bytes(nonce)));
                var cipher = jni.StaticObject("javax/crypto/Cipher", "getInstance", "(Ljava/lang/String;)Ljavax/crypto/Cipher;",
                    Ref(jni.String("AES/GCM/NoPadding")));
                jni.Void(cipher, "init", "(ILjava/security/Key;Ljava/security/spec/AlgorithmParameterSpec;)V", Int(2), Ref(key), Ref(parameters));
                jni.Void(cipher, "updateAAD", "([B)V", Ref(jni.Bytes(AssociatedData)));
                var result = jni.Object(cipher, "doFinal", "([B)[B", Ref(jni.Bytes(ciphertext)));
                try { return jni.ReadBytes(result); }
                finally { jni.ClearBytes(result); }
            }
            catch (CryptographicException exception)
            {
                throw new CryptographicException("无法解密会话：文件可能来自其他设备、已损坏或密钥已丢失，请重新登录。", exception);
            }
        }
    }

    internal static void DeleteKey()
    {
        lock (Gate)
        {
            using var jni = CreateScope();
            var store = OpenStore(jni);
            var alias = jni.String(Alias);
            if (jni.Boolean(store, "containsAlias", "(Ljava/lang/String;)Z", Ref(alias)))
                jni.Void(store, "deleteEntry", "(Ljava/lang/String;)V", Ref(alias));
        }
    }

    private static nint OpenStore(AndroidJniScope jni)
    {
        var store = jni.StaticObject("java/security/KeyStore", "getInstance", "(Ljava/lang/String;)Ljava/security/KeyStore;",
            Ref(jni.String("AndroidKeyStore")));
        jni.Void(store, "load", "(Ljava/io/InputStream;[C)V", Ref(0), Ref(0));
        return store;
    }

    private static nint GetKey(AndroidJniScope jni, bool create)
    {
        var store = OpenStore(jni);
        var alias = jni.String(Alias);
        if (jni.Boolean(store, "containsAlias", "(Ljava/lang/String;)Z", Ref(alias)))
            return jni.Object(store, "getKey", "(Ljava/lang/String;[C)Ljava/security/Key;", Ref(alias), Ref(0));
        if (!create) throw new CryptographicException("本设备没有此会话的密钥。");

        var builder = jni.New(Builder, "(Ljava/lang/String;I)V", Ref(alias), Int(3)); // ENCRYPT | DECRYPT
        jni.Object(builder, "setKeySize", "(I)" + BuilderResult, Int(256));
        jni.Object(builder, "setBlockModes", "([Ljava/lang/String;)" + BuilderResult, Ref(jni.StringArray("GCM")));
        jni.Object(builder, "setEncryptionPaddings", "([Ljava/lang/String;)" + BuilderResult, Ref(jni.StringArray("NoPadding")));
        jni.Object(builder, "setRandomizedEncryptionRequired", "(Z)" + BuilderResult, Int(1));
        var spec = jni.Object(builder, "build", "()Landroid/security/keystore/KeyGenParameterSpec;");
        var generator = jni.StaticObject("javax/crypto/KeyGenerator", "getInstance",
            "(Ljava/lang/String;Ljava/lang/String;)Ljavax/crypto/KeyGenerator;", Ref(jni.String("AES")), Ref(jni.String("AndroidKeyStore")));
        jni.Void(generator, "init", "(Ljava/security/spec/AlgorithmParameterSpec;)V", Ref(spec));
        return jni.Object(generator, "generateKey", "()Ljavax/crypto/SecretKey;");
    }

    private static AndroidJniScope CreateScope()
    {
        try { return new AndroidJniScope(); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new CryptographicException("当前环境无法访问 Android Keystore，本次登录不会保存到磁盘。", exception);
        }
    }

    internal sealed record SealedSession(byte[] Nonce, byte[] Ciphertext);
}
