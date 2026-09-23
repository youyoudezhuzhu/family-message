using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FamilyAgent;

/// <summary>
/// 消息气泡（Material Design 3 风格）。
///
///   网页发来的  [头像] [气泡]  ← 靠左
///   本机回复的        [气泡] [头像]  ← 靠右
///
/// **颜色按昵称分配**：同一个昵称永远是同一个颜色，跟「从哪端发来」无关
/// （取色算法见 MdTheme.NickColor / docs/DESIGN-TOKENS.md §3）。
/// 左右位置只表达「谁发的」，不表达颜色含义。
///
/// 新插入的卡片播放「淡入 + 从下方滑入 + 微放大」动画。
/// </summary>
public sealed class MessageCard : Grid
{
    private readonly TextBlock _sender;
    private readonly TextBlock _content;
    private readonly TextBlock _time;
    private readonly Border _bubble;
    private readonly double _baseFontSize;
    private readonly Color _nick;
    private static bool _emojiWarned;

    /// <summary>
    /// 建一个消息文本控件。
    ///
    /// 优先用 Emoji.Wpf 的 TextBlock —— WPF 原生 TextBlock 会把 emoji 渲染成黑白轮廓。
    /// 但那个库内部要解析字体的 COLR/CPAL 彩色图层，**一旦它抛异常就会把整个进程带走**
    /// （v0.6.0 的闪退就是这样）。所以这里兜住：库不可用时退回系统 TextBlock，
    /// emoji 变黑白，但应用不会崩。首次失败会写进日志，方便定位。
    /// </summary>
    private static TextBlock NewTextBlock(string text, double fontSize, Brush foreground)
    {
        try
        {
            var tb = new Emoji.Wpf.TextBlock
            {
                FontSize = fontSize,
                Foreground = foreground,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = fontSize * 1.35,
            };
            tb.Text = text;      // Text 放最后设：库在处理文本时会用到字体相关属性
            return tb;
        }
        catch (Exception ex)
        {
            if (!_emojiWarned)
            {
                _emojiWarned = true;
                AgentLog.Write("!! 彩色 emoji 不可用，已退回系统字体（不影响使用）："
                               + ex.GetType().Name + " " + ex.Message);
            }
            return new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                Foreground = foreground,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = fontSize * 1.35,
            };
        }
    }

    public long MessageId { get; }
    public bool IsOut { get; }
    public string RawContent { get; }

    public MessageCard(long messageId, string senderName, string content, string time, bool isOut)
    {
        MessageId = messageId;
        IsOut = isOut;
        RawContent = content ?? "";

        HorizontalAlignment = isOut ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Margin = new Thickness(0, 0, 0, 14);

        var name = string.IsNullOrWhiteSpace(senderName) ? "家庭消息" : senderName;
        _nick = MdTheme.NickColor(name);

        // 基准字号按内容长度自适应，长消息不至于把气泡撑爆
        var len = RawContent.Length;
        // 全部取自 MD3 Type Scale 的档位，不再随手写字号（规范 §6）
        _baseFontSize = len <= 12 ? MdTheme.Type.HeadlineLarge     // 32 —— 强提醒，字要大
            : len <= 28 ? MdTheme.Type.HeadlineMedium              // 28
            : len <= 60 ? MdTheme.Type.HeadlineSmall               // 24
            : len <= 140 ? MdTheme.Type.TitleLarge                 // 22
            : MdTheme.Type.BodyLarge;                              // 16 —— 长消息回到正文号

        _sender = NewTextBlock(name, 14, new SolidColorBrush(_nick));
        _sender.FontWeight = FontWeights.Medium;
        _sender.Margin = new Thickness(0, 0, 0, 4);

        _content = NewTextBlock(RawContent, _baseFontSize, MdTheme.OnSurface);

        _time = new TextBlock
        {
            Text = time ?? "",
            FontSize = MdTheme.Type.LabelSmall,
            Foreground = MdTheme.TimeText,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var stack = new StackPanel();
        stack.Children.Add(_sender);
        stack.Children.Add(_content);
        stack.Children.Add(_time);

        // 气泡：中性底 + 昵称色淡染（约 10%），左侧/右侧一条昵称色强调条
        var bubbleBg = MdTheme.Blend(MdTheme.SurfaceC.Color, _nick, 0.10);

        _bubble = new Border
        {
            Child = stack,
            Background = new SolidColorBrush(bubbleBg),
            CornerRadius = new CornerRadius(MdTheme.Shape.Large, MdTheme.Shape.Large, MdTheme.Shape.Large, MdTheme.Shape.ExtraSmall),
            Padding = new Thickness(18, 12, 18, 10),
            BorderThickness = isOut ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0),
            BorderBrush = new SolidColorBrush(_nick),
            MaxWidth = 660,
            SnapsToDevicePixels = true,
        };

        if (isOut)
        {
            _bubble.CornerRadius = new CornerRadius(MdTheme.Shape.Large, MdTheme.Shape.Large, MdTheme.Shape.ExtraSmall, MdTheme.Shape.Large);
        }

        var avatar = BuildAvatar(name, _nick);
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
        };

        if (isOut)
        {
            row.Children.Add(_bubble);
            row.Children.Add(avatar);
        }
        else
        {
            row.Children.Add(avatar);
            row.Children.Add(_bubble);
        }

        Children.Add(row);
    }

    private static Border BuildAvatar(string name, Color nick)
    {
        var initial = "?";
        try
        {
            if (!string.IsNullOrEmpty(name))
                initial = StringInfo.GetNextTextElement(name, 0);
        }
        catch
        {
            initial = name.Length > 0 ? name[..1] : "?";
        }

        return new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(MdTheme.Shape.Full),
            Background = new SolidColorBrush(MdTheme.Blend(MdTheme.SurfaceHigh.Color, nick, 0.22)),
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = initial,
                FontSize = MdTheme.Type.TitleLarge,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(nick),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>
    /// 换主题后刷新与主题相关的颜色。
    /// 画刷现在是不可变的（旧实例可能已被 WPF 冻结），所以这里重新取当前实例，
    /// 而不是去改旧画刷的颜色。
    /// </summary>
    public void ApplyTheme()
    {
        _content.Foreground = MdTheme.OnSurface;
        _time.Foreground = MdTheme.TimeText;
        _bubble.Background = new SolidColorBrush(
            MdTheme.Blend(MdTheme.SurfaceC.Color, _nick, 0.10));
    }

    /// <summary>按「距最新一条的距离」调整字号与高亮。distance = 0 表示最新。</summary>
    public void ApplyProminence(bool isNewest, int distance, bool animate)
    {
        var factor = isNewest ? 1.0 : Math.Max(0.80, Math.Pow(0.90, distance));
        var target = _baseFontSize * factor;

        AnimateFont(_content, target, animate);
        _content.LineHeight = Math.Max(20, target * 1.35);

        var targetOpacity = isNewest ? 1.0 : Math.Max(0.68, 1.0 - 0.09 * distance);
        if (animate)
        {
            var anim = new DoubleAnimation(Opacity, targetOpacity, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Opacity = targetOpacity;
            BeginAnimation(OpacityProperty, anim);
        }
        else
        {
            Opacity = targetOpacity;
        }
    }

    private static void AnimateFont(TextBlock block, double target, bool animate)
    {
        var current = block.FontSize;
        if (!animate || Math.Abs(current - target) < 0.5)
        {
            block.FontSize = target;
            return;
        }

        block.BeginAnimation(TextBlock.FontSizeProperty,
            new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>插入动画：淡入 + 从下方 40px 滑入 + 0.97→1.0 微放大。</summary>
    public void PlayEnterAnimation(double targetOpacity)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(340);

        Opacity = targetOpacity;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, targetOpacity, duration)
        {
            EasingFunction = ease,
        });

        var translate = new TranslateTransform();
        var scale = new ScaleTransform(1.0, 1.0);
        RenderTransform = new TransformGroup { Children = { scale, translate } };

        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(40, 0, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.97, 1.0, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.97, 1.0, duration) { EasingFunction = ease });
    }
}
