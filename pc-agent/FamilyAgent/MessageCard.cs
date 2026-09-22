using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FamilyAgent;

/// <summary>
/// 弹窗左栏里的一条消息卡片。
///
/// 堆叠规则：越新的消息字号越大、越醒目；越靠上的（越早的）逐级缩小并降低不透明度。
/// 新插入的卡片会播放「淡入 + 从下方滑入」动画，避免多条消息连发时闪屏。
/// </summary>
public sealed class MessageCard : Border
{
    private static readonly Color Accent = Color.FromRgb(0x5A, 0xC8, 0xFA);
    private static readonly Color NewestBg = Color.FromRgb(0x16, 0x22, 0x3A);
    private static readonly Color OlderBg = Color.FromRgb(0x13, 0x1A, 0x26);

    private readonly TextBlock _sender;
    private readonly TextBlock _content;
    private readonly TextBlock _time;
    private readonly double _baseFontSize;

    public long MessageId { get; }
    public string RawContent { get; }

    public MessageCard(long messageId, string senderName, string content, string time)
    {
        MessageId = messageId;
        RawContent = content ?? "";

        // 基准字号按内容长度自适应，长消息不至于爆出屏幕
        var len = RawContent.Length;
        _baseFontSize = len <= 10 ? 58
            : len <= 22 ? 46
            : len <= 48 ? 36
            : len <= 100 ? 28
            : 22;

        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(22, 16, 22, 16);
        Margin = new Thickness(0, 0, 0, 12);
        BorderThickness = new Thickness(2);
        SnapsToDevicePixels = true;

        _sender = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(senderName) ? "家庭消息" : senderName,
            FontSize = _baseFontSize * 0.40,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Accent),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };

        _content = new TextBlock
        {
            Text = RawContent,
            FontSize = _baseFontSize,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF6, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = _baseFontSize * 1.2,
        };

        _time = new TextBlock
        {
            Text = time ?? "",
            FontSize = Math.Max(12, _baseFontSize * 0.26),
            Foreground = new SolidColorBrush(Color.FromRgb(0x76, 0x86, 0xA3)),
            Margin = new Thickness(0, 8, 0, 0),
        };

        var panel = new StackPanel();
        panel.Children.Add(_sender);
        panel.Children.Add(_content);
        if (!string.IsNullOrEmpty(_time.Text))
            panel.Children.Add(_time);
        Child = panel;

        ApplyProminence(isNewest: true, distance: 0, animate: false);
    }

    /// <summary>
    /// 按「距最新一条的距离」调整字号 / 底色 / 描边。
    /// distance = 0 表示就是最新那条。
    /// </summary>
    public void ApplyProminence(bool isNewest, int distance, bool animate)
    {
        var factor = isNewest ? 1.0 : Math.Max(0.45, Math.Pow(0.80, distance));
        var targetContent = _baseFontSize * factor;
        var targetSender = Math.Max(14, targetContent * 0.40);
        var targetTime = Math.Max(11, targetContent * 0.26);
        var targetOpacity = isNewest ? 1.0 : Math.Max(0.55, 1.0 - 0.12 * distance);

        AnimateFont(_content, targetContent, animate);
        AnimateFont(_sender, targetSender, animate);
        _time.FontSize = targetTime;

        Background = new SolidColorBrush(isNewest ? NewestBg : OlderBg);
        BorderBrush = isNewest ? new SolidColorBrush(Accent) : Brushes.Transparent;

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

        var anim = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        block.BeginAnimation(TextBlock.FontSizeProperty, anim);
    }

    /// <summary>插入时播放：淡入 + 从下方 46px 滑入 + 轻微放大。</summary>
    public void PlayEnterAnimation(double targetOpacity)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(360);

        Opacity = targetOpacity;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, targetOpacity, duration)
        {
            EasingFunction = ease,
        });

        var translate = new TranslateTransform();
        var scale = new ScaleTransform(1.0, 1.0);
        RenderTransform = new TransformGroup { Children = { scale, translate } };

        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(46, 0, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.97, 1.0, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.97, 1.0, duration) { EasingFunction = ease });
    }
}
