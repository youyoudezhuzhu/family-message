using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace FamilyAgent;

/// <summary>
/// 桌面截图。按需求「请求一次 → 截一张图」实现，不做实时视频/远程控制。
/// 截主显示器（多屏时不会拼成超宽图），JPEG 压缩后 base64 回传。
/// </summary>
public static class ScreenCapture
{
    public static string LastError { get; private set; } = "";

    /// <summary>截取主显示器，返回 (base64, width, height)；失败返回 (null, 0, 0)。</summary>
    public static (string? Base64, int Width, int Height) CaptureJpeg(int quality = 75)
    {
        try
        {
            var screen = Screen.PrimaryScreen;
            if (screen is null)
            {
                LastError = "找不到主显示器";
                return (null, 0, 0);
            }

            var b = screen.Bounds;
            using var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(b.X, b.Y, 0, 0, new Size(b.Width, b.Height),
                    CopyPixelOperation.SourceCopy);
            }

            using var ms = new MemoryStream();
            var codec = GetJpegCodec();
            if (codec is not null)
            {
                using var encParams = new EncoderParameters(1);
                encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                bmp.Save(ms, codec, encParams);
            }
            else
            {
                bmp.Save(ms, ImageFormat.Jpeg);
            }

            LastError = "";
            return (Convert.ToBase64String(ms.ToArray()), b.Width, b.Height);
        }
        catch (Exception ex)
        {
            // 锁屏 / 会话被切换时常见：CopyFromScreen 抛异常或返回黑屏
            LastError = ex.Message;
            return (null, 0, 0);
        }
    }

    private static ImageCodecInfo? GetJpegCodec()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
                return codec;
        }
        return null;
    }
}
