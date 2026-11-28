using Godot;
using FileAccess = Godot.FileAccess;

namespace WaningOath;

/// <summary>
/// 从 `res://` 取一张纹理，**源码树与导出包两条路都走通**。
/// </summary>
/// <remarks>
/// <b>为什么需要这一层</b>：本项目的图**不走 Godot 的导入系统**（编辑器在本机会挂住），
/// 所以源码树里是 `Image.LoadFromFile` 直读原始 PNG 字节。
/// 但**导出包不带原始 PNG** —— 带的是导入后的 `.ctex`，`FileAccess` 根本看不到原文件。
/// ⇒ 两条路：能读到字节就读字节；读不到就问 <see cref="ResourceLoader"/> 要导入后的纹理。
/// <para>
/// 任何一步失败都返回 <c>null</c>（调用方软着陆），绝不抛。
/// </para>
/// </remarks>
internal static class TextureLoader
{
    /// <summary>取一张纹理；取不到返回 <c>null</c>。</summary>
    public static Texture2D? FromRes(string path)
    {
        try
        {
            if (FileAccess.FileExists(path))
            {
                Image? img = Image.LoadFromFile(path);
                if (img is not null) return ImageTexture.CreateFromImage(img);
            }

            // 导出包：原始文件不在 pck 里，只有导入后的纹理。
            return ResourceLoader.Load<Texture2D>(path);
        }
        catch
        {
            return null;
        }
    }
}
