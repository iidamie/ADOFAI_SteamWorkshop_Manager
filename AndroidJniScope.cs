using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SteamWorkshopManager;

// JNI table slots follow the standard JNI 1.6 ABI. No references cross this scope
// or its owning thread; a thread attached here is detached here as well.
internal sealed unsafe class AndroidJniScope : IDisposable
{
    private readonly nint _vm;
    private readonly nint _env;
    private readonly nint* _functions;
    private readonly bool _attached;
    private readonly Dictionary<string, nint> _classes = new(StringComparer.Ordinal);
    private bool _disposed;

    internal AndroidJniScope()
    {
        _vm = GetJavaVm();
        if (_vm == 0) throw Unavailable();
        var vmFunctions = *(nint**)_vm;
        nint env = 0;
        var result = ((delegate* unmanaged[Cdecl]<nint, nint*, int, int>)vmFunctions[6])(_vm, &env, 0x00010006);
        if (result == -2) // JNI_EDETACHED
        {
            result = ((delegate* unmanaged[Cdecl]<nint, nint*, nint, int>)vmFunctions[4])(_vm, &env, 0);
            _attached = result == 0;
        }
        if (result != 0 || env == 0) throw Unavailable();
        _env = env;
        _functions = *(nint**)_env;
        try
        {
            Check();
            var pushed = ((delegate* unmanaged[Cdecl]<nint, int, int>)_functions[19])(_env, 128);
            Check();
            if (pushed != 0) throw Unavailable();
        }
        catch
        {
            Detach();
            throw;
        }
    }

    [DllImport("modmanager", EntryPoint = "jnihelper_get_jvm", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint GetJavaVm();

    internal nint Class(string name)
    {
        if (_classes.TryGetValue(name, out var existing)) return existing;
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        nint result;
        fixed (byte* data = bytes)
            result = ((delegate* unmanaged[Cdecl]<nint, byte*, nint>)_functions[6])(_env, data);
        Check();
        _classes[name] = Required(result);
        return result;
    }

    private nint Method(nint type, string name, string signature, bool isStatic = false)
    {
        var methodName = Encoding.UTF8.GetBytes(name + "\0");
        var methodSignature = Encoding.UTF8.GetBytes(signature + "\0");
        nint result;
        fixed (byte* nameData = methodName)
        fixed (byte* signatureData = methodSignature)
            result = ((delegate* unmanaged[Cdecl]<nint, nint, byte*, byte*, nint>)_functions[isStatic ? 113 : 33])(
                _env, Required(type), nameData, signatureData);
        Check();
        return Required(result);
    }

    private nint ObjectClass(nint instance)
    {
        var result = ((delegate* unmanaged[Cdecl]<nint, nint, nint>)_functions[31])(_env, Required(instance));
        Check();
        return Required(result);
    }

    internal nint New(string className, string signature, params Value[] arguments)
    {
        var type = Class(className);
        var method = Method(type, "<init>", signature);
        nint result;
        fixed (Value* args = arguments)
            result = ((delegate* unmanaged[Cdecl]<nint, nint, nint, Value*, nint>)_functions[30])(_env, type, method, args);
        Check();
        return Required(result);
    }

    internal nint StaticObject(string className, string name, string signature, params Value[] arguments)
    {
        var type = Class(className);
        var method = Method(type, name, signature, isStatic: true);
        nint result;
        fixed (Value* args = arguments)
            result = ((delegate* unmanaged[Cdecl]<nint, nint, nint, Value*, nint>)_functions[116])(_env, type, method, args);
        Check();
        return Required(result);
    }

    internal nint Object(nint instance, string name, string signature, params Value[] arguments)
    {
        var method = Method(ObjectClass(instance), name, signature);
        nint result;
        fixed (Value* args = arguments)
            result = ((delegate* unmanaged[Cdecl]<nint, nint, nint, Value*, nint>)_functions[36])(_env, instance, method, args);
        Check();
        return Required(result);
    }

    internal bool Boolean(nint instance, string name, string signature, params Value[] arguments)
    {
        var method = Method(ObjectClass(instance), name, signature);
        byte result;
        fixed (Value* args = arguments)
            result = ((delegate* unmanaged[Cdecl]<nint, nint, nint, Value*, byte>)_functions[39])(_env, instance, method, args);
        Check();
        return result != 0;
    }

    internal void Void(nint instance, string name, string signature, params Value[] arguments)
    {
        var method = Method(ObjectClass(instance), name, signature);
        fixed (Value* args = arguments)
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, Value*, void>)_functions[63])(_env, instance, method, args);
        Check();
    }

    internal nint String(string text)
    {
        nint result;
        fixed (char* data = text)
            result = ((delegate* unmanaged[Cdecl]<nint, char*, int, nint>)_functions[163])(_env, data, text.Length);
        Check();
        return Required(result);
    }

    internal nint StringArray(string element)
    {
        var value = String(element);
        var result = ((delegate* unmanaged[Cdecl]<nint, int, nint, nint, nint>)_functions[172])(_env, 1, Class("java/lang/String"), value);
        Check();
        return Required(result);
    }

    internal nint Bytes(byte[] bytes)
    {
        var result = ((delegate* unmanaged[Cdecl]<nint, int, nint>)_functions[176])(_env, bytes.Length);
        Check();
        Required(result);
        fixed (byte* data = bytes)
            ((delegate* unmanaged[Cdecl]<nint, nint, int, int, byte*, void>)_functions[208])(_env, result, 0, bytes.Length, data);
        Check();
        return result;
    }

    internal byte[] ReadBytes(nint array)
    {
        var length = ((delegate* unmanaged[Cdecl]<nint, nint, int>)_functions[171])(_env, Required(array));
        Check();
        if (length < 0 || length > 65552) throw new CryptographicException("设备密钥库返回了无效的会话数据。");
        var result = new byte[length];
        try
        {
            fixed (byte* data = result)
                ((delegate* unmanaged[Cdecl]<nint, nint, int, int, byte*, void>)_functions[200])(_env, array, 0, length, data);
            Check();
            return result;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(result);
            throw;
        }
    }

    internal void ClearBytes(nint array)
    {
        if (array == 0) return;
        try
        {
            var length = ((delegate* unmanaged[Cdecl]<nint, nint, int>)_functions[171])(_env, array);
            Check();
            if (length <= 0 || length > 65552) return;
            var zeros = new byte[length];
            fixed (byte* data = zeros)
                ((delegate* unmanaged[Cdecl]<nint, nint, int, int, byte*, void>)_functions[208])(_env, array, 0, length, data);
            Check();
        }
        catch (CryptographicException) { }
    }

    private void Check()
    {
        if (((delegate* unmanaged[Cdecl]<nint, byte>)_functions[228])(_env) == 0) return;
        ((delegate* unmanaged[Cdecl]<nint, void>)_functions[17])(_env);
        // Never call ExceptionDescribe: Java exceptions must not dump session data.
        throw Unavailable();
    }

    private static nint Required(nint reference) => reference != 0 ? reference : throw Unavailable();
    private static CryptographicException Unavailable() => new("设备密钥库无法完成会话保护，请重新登录或稍后重试。");

    private void Detach()
    {
        if (_attached)
            ((delegate* unmanaged[Cdecl]<nint, int>)(*(nint**)_vm)[5])(_vm);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { ((delegate* unmanaged[Cdecl]<nint, nint, nint>)_functions[20])(_env, 0); }
        finally { Detach(); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    internal struct Value
    {
        [FieldOffset(0)] private nint _reference;
        [FieldOffset(0)] private int _integer;
        internal static Value Ref(nint value) => new() { _reference = value };
        internal static Value Int(int value) => new() { _integer = value };
    }
}
