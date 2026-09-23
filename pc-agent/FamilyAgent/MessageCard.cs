using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FamilyAgent;

/// <summary>
/// 弹窗主区域里的一条消息，按聊天软件的样子排版：
///
///   收到（网页发来的）  [头像] [气泡]  ← 靠左
///   发出（本机回复的）        [气泡] [头像]  ← 靠右
///
/// 新插入的卡片会播放「淡入 + 从下方滑入 + 轻微放大」动画。
/// 越新的气泡略大一点，越旧的逐级回落（0.90 倍/级，下限 0.80），
/// 保留层次感但不至于像字号乱跳。
/// </summary>
public sealed class MessageCard : Grid
{
    private static readonly Color Accent = Color.FromRgb(0x5A, 0xC8, 0xFA);
    private static readonly Color InBg = Color.FromRgb(0x16, 0x1D, 0x2B);
    private static readonly Color OutBg = Color.FromRgb(0x14, 0x2C, 0x42);
    private static readonly Color Gold = Color.FromRgb(0xC9, 0xA2, 0x4D);

    private readonly TextBlock _sender;
    private readonly TextBlock _content;
    private readonly TextBlock _time;
    private readonly Border _bubble;
    private readonly double _baseFontSize;

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

        // 基准字号按内容长度自适应，长消息不至于把气泡撑爆
        var len = RawContent.Length;
        _baseFontSize = len <= 12 ? 30
            : len <= 28 ? 26
            : len <= 60 ? 22
            : len <= 140 ? 19
            : 17;

        var name = string.IsNullOrWhiteSpace(senderName) ? "家庭消息" : senderName;

        _sender = new TextBlock
        {
            Text = name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(isOut ? Gold : Accent),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };

        _content = new TextBlock
        {
            Text = RawContent,
            FontSize = _baseFontSize,
            Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xF2, 0xFC)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = _baseFontSize * 1.35,
        };

        _time = new TextBlock
        {
            Text = time ?? "",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x74, 0x84, 0xA1)),
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var stack = new StackPanel();
        stack.Children.Add(_sender);
        stack.Children.Add(_content);
        stack.Children.Add(_time);

        _bubble = new Border
        {
            Child = stack,
            Background = new SolidColorBrush(isOut ? OutBg : InBg),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 12, 18, 10),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            MaxWidth = 640,
            SnapsToDevicePixels = true,
        };

        var avatar = BuildAvatar(name, isOut);
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

    private static Border BuildAvatar(string name, bool isOut)
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
            CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(isOut
                ? Color.FromRgb(0x24, 0x33, 0x24)
                : Color.FromRgb(0x1B, 0x2A, 0x42)),
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = initial,
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(isOut ? Gold : Accent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>按「距最新一条的距离」调整字号与高亮。distance = 0 表示最新。</summary>
    public void ApplyProminence(bool isNewest, int distance, bool animate)
    {
        var factor = isNewest ? 1.0 : Math.Max(0.80, Math.Pow(0.90, distance));
        var target = _baseFontSize * factor;

        AnimateFont(_content, target, animate);
        _content.LineHeight = Math.Max(20, target * 1.35);

        _bubble.BorderBrush = isNewest
            ? new SolidColorBrush(IsOut ? Gold : Accent)
            : Brushes.Transparent;

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
