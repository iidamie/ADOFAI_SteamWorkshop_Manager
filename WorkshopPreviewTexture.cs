using System.Runtime.InteropServices;
using ImGuiNET;

namespace SteamWorkshopManager;

internal sealed class WorkshopPreviewTexture : IDisposable
{
    private readonly nint _context;
    private uint _texture;
    public nint Id => (nint)_texture;
    public bool IsCurrentContext => GetContext() == _context;

    private WorkshopPreviewTexture(uint texture, nint context) { _texture = texture; _context = context; }

    public static WorkshopPreviewTexture? TryCreate(byte[] pixels, int width, int height)
    {
        try
        {
            var backend = ImGui.GetIO().BackendRendererName.ToString() ?? string.Empty;
            if (!backend.Contains("OpenGL", StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch { return null; }
        var context = GetContext();
        if (context == 0) return null;
        uint texture = 0;
        int previousTexture = 0, alignment = 4, rowLength = 0, skipRows = 0, skipPixels = 0, unpackBuffer = 0;
        bool captured = false, adopted = false;
        try
        {
            Gl.GetInteger(0x8069, out previousTexture); // GL_TEXTURE_BINDING_2D, current texture unit
            Gl.GetInteger(0x0CF5, out alignment); // GL_UNPACK_ALIGNMENT
            Gl.GetInteger(0x0CF2, out rowLength); // GL_UNPACK_ROW_LENGTH
            Gl.GetInteger(0x0CF3, out skipRows); // GL_UNPACK_SKIP_ROWS
            Gl.GetInteger(0x0CF4, out skipPixels); // GL_UNPACK_SKIP_PIXELS
            Gl.GetInteger(0x88EF, out unpackBuffer); // GL_PIXEL_UNPACK_BUFFER_BINDING
            captured = true;
            for (var i = 0; i < 8 && Gl.GetError() != 0; i++) { }
            Gl.GenTextures(1, out texture);
            if (texture == 0) return null;
            Gl.BindTexture(0x0DE1, texture);
            Gl.TexParameter(0x0DE1, 0x2801, 0x2601); // min/mag: linear
            Gl.TexParameter(0x0DE1, 0x2800, 0x2601);
            Gl.TexParameter(0x0DE1, 0x2802, 0x812F); // clamp to edge
            Gl.TexParameter(0x0DE1, 0x2803, 0x812F);
            Gl.PixelStore(0x0CF5, 1);
            Gl.PixelStore(0x0CF2, 0);
            Gl.PixelStore(0x0CF3, 0);
            Gl.PixelStore(0x0CF4, 0);
            Gl.BindBuffer(0x88EC, 0); // Client memory, not an offset into the game's PBO.
            Gl.TexImage(0x0DE1, 0, 0x1908, width, height, 0, 0x1908, 0x1401, pixels);
            if (Gl.GetError() != 0) return null;
            adopted = true;
            return new WorkshopPreviewTexture(texture, context);
        }
        catch { return null; }
        finally
        {
            if (captured)
            {
                try
                {
                    Gl.BindTexture(0x0DE1, (uint)Math.Max(0, previousTexture));
                    Gl.PixelStore(0x0CF5, alignment is 1 or 2 or 4 or 8 ? alignment : 4);
                    Gl.PixelStore(0x0CF2, Math.Max(0, rowLength));
                    Gl.PixelStore(0x0CF3, Math.Max(0, skipRows));
                    Gl.PixelStore(0x0CF4, Math.Max(0, skipPixels));
                    Gl.BindBuffer(0x88EC, (uint)Math.Max(0, unpackBuffer));
                }
                catch { }
            }
            if (!adopted && texture != 0) Delete(ref texture);
        }
    }

    private static nint GetContext()
    {
        try { return Gl.CurrentContext(); }
        catch { return 0; }
    }

    private static void Delete(ref uint texture)
    {
        try { Gl.DeleteTextures(1, ref texture); } catch { }
    }

    public void Dispose()
    {
        if (_texture == 0) return;
        // Never delete a reused ID from another context after the host recreates EGL.
        if (IsCurrentContext) Delete(ref _texture);
        _texture = 0;
    }

    private static class Gl
    {
        [DllImport("libEGL.so", EntryPoint = "eglGetCurrentContext")] internal static extern nint CurrentContext();
        [DllImport("libGLESv3.so", EntryPoint = "glGetIntegerv")] internal static extern void GetInteger(uint name, out int value);
        [DllImport("libGLESv3.so", EntryPoint = "glGetError")] internal static extern uint GetError();
        [DllImport("libGLESv3.so", EntryPoint = "glBindTexture")] internal static extern void BindTexture(uint target, uint texture);
        [DllImport("libGLESv3.so", EntryPoint = "glBindBuffer")] internal static extern void BindBuffer(uint target, uint buffer);
        [DllImport("libGLESv3.so", EntryPoint = "glGenTextures")] internal static extern void GenTextures(int count, out uint texture);
        [DllImport("libGLESv3.so", EntryPoint = "glDeleteTextures")] internal static extern void DeleteTextures(int count, ref uint texture);
        [DllImport("libGLESv3.so", EntryPoint = "glPixelStorei")] internal static extern void PixelStore(uint name, int value);
        [DllImport("libGLESv3.so", EntryPoint = "glTexParameteri")] internal static extern void TexParameter(uint target, uint name, int value);
        [DllImport("libGLESv3.so", EntryPoint = "glTexImage2D")]
        internal static extern void TexImage(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, byte[] pixels);
    }
}
