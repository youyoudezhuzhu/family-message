#!/usr/bin/env python3
"""Windows Agent 的静态引用检查 —— 在编译前跑，提前抓出「引用不存在的成员」这类错误。

为什么需要它：本地没有 .NET 工具链，改 C# 只能靠云端编译验证，
一轮往返好几分钟。而这些错误（成员被改名/删除、XAML 引用了不存在的常量）
纯静态就能查出来，没必要浪费一轮编译。

检查项：
  1. .cs 里所有 MdTheme.X / MdTheme.Type.X / MdTheme.Shape.X 引用都存在
  2. .xaml 里所有 local:MdTheme.X 引用都存在
     （特别注意：XAML 的 x:Static **不支持嵌套类型**，
      {x:Static local:MdTheme.Shape.Full} 会报 MC3050，必须用扁平常量）
  3. XAML 里 x:Name 的控件，.cs 里用到的都有定义
  4. XAML 里绑的事件处理函数都有实现
  5. using 覆盖：用到的类型是否有对应 using（本项目 ImplicitUsings=disable）
  6. 花括号平衡
  7. XAML 是合法 XML

用法：python3 tools/check_agent_refs.py [pc-agent/FamilyAgent]
退出码 0 = 全通过，1 = 有问题。
"""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(sys.argv[1] if len(sys.argv) > 1 else "pc-agent/FamilyAgent")

errors: list[str] = []
notes: list[str] = []


def need(cond: bool, msg: str) -> None:
    if not cond:
        errors.append(msg)


def main() -> int:
    theme_path = ROOT / "MdTheme.cs"
    if not theme_path.exists():
        print(f"找不到 {theme_path}")
        return 1
    theme = theme_path.read_text(encoding="utf-8")

    # ── 收集 MdTheme 的公开成员 ──
    members = set(re.findall(r'public (?:static readonly|const|static) [\w<>\.\[\]\?]+ (\w+)', theme))
    members |= set(re.findall(r'public (?:static )?[\w<>\[\]\?]+ (\w+) \{ get', theme))
    members |= set(re.findall(r'public (?:static )?[\w<>\[\]\?]+ (\w+)\(', theme))
    members |= set(re.findall(r'public static class (\w+)', theme))

    tsec = theme.split('public static class Type')[1].split('public static class Shape')[0]
    ssec = theme.split('public static class Shape')[1].split('// ── 给 XAML 用')[0]
    type_members = set(re.findall(r'public const double (\w+) =', tsec))
    shape_members = set(re.findall(r'public const double (\w+) =', ssec))

    # ── 1/5/6：逐个 .cs 文件 ──
    NEEDS = {
        'Dictionary<': 'System.Collections.Generic',
        'List<': 'System.Collections.Generic',
        'IReadOnlyList<': 'System.Collections.Generic',
        'SolidColorBrush': 'System.Windows.Media',
        'Color.FromArgb': 'System.Windows.Media',
        'ColorConverter': 'System.Windows.Media',
        'Registry': 'Microsoft.Win32',
        '.Select(': 'System.Linq',
        '.Where(': 'System.Linq',
        '.FirstOrDefault(': 'System.Linq',
        'StringInfo': 'System.Globalization',
        'Path.': 'System.IO',
    }

    for f in sorted(ROOT.glob("*.cs")):
        txt = f.read_text(encoding="utf-8")

        for m in set(re.findall(r'(?<![\w.])MdTheme\.(\w+)', txt)):
            if m not in members and m not in ("Type", "Shape"):
                errors.append(f"{f.name}: 引用了不存在的 MdTheme.{m}")
        for m in set(re.findall(r'MdTheme\.Type\.(\w+)', txt)):
            if m not in type_members:
                errors.append(f"{f.name}: 引用了不存在的 MdTheme.Type.{m}")
        for m in set(re.findall(r'MdTheme\.Shape\.(\w+)', txt)):
            if m not in shape_members:
                errors.append(f"{f.name}: 引用了不存在的 MdTheme.Shape.{m}")

        lines = txt.splitlines()
        usings = {
            re.sub(r'^\s*using\s+(?:static\s+)?|;\s*$', '', ln)
            for ln in lines if ln.strip().startswith("using ")
        }
        body = "\n".join(ln for ln in lines if not ln.strip().startswith("using "))
        for token, ns in NEEDS.items():
            if token in body and ns not in usings:
                errors.append(f"{f.name}: 用到 {token} 但缺 using {ns}")

        if txt.count('{') != txt.count('}'):
            errors.append(f"{f.name}: 花括号不平衡 {txt.count('{')}/{txt.count('}')}")

    # ── 2/3/4/7：XAML ──
    # 控件名检查用到的「非控件」白名单：这些是 .NET 的类型/命名空间，
    # 也会以 X.Something 的形式出现在代码里，但显然不是 x:Name。
    NOT_A_CONTROL = {
        # 命名空间段
        "System", "Microsoft", "Windows", "Forms", "Media", "Input", "Threading",
        "Tasks", "Diagnostics", "IO", "Text", "Json", "Collections", "Generic",
        "Linq", "Globalization", "Net", "Http", "Drawing", "Security", "Runtime",
        # 常用 BCL / WPF 类型
        "DispatcherPriority", "Brushes", "MessageBox", "MessageBoxButton",
        "MessageBoxImage", "MessageBoxResult", "Visibility", "HorizontalAlignment",
        "VerticalAlignment", "Thickness", "GridLength", "WindowState", "WindowStyle",
        "ResizeMode", "Task", "Math", "Environment", "Registry", "Color", "Colors",
        "Convert", "String", "Char", "Guid", "TimeSpan", "DateTime", "Application",
        "Clipboard", "Thread", "JsonSerializer", "JsonSerializerOptions", "Path",
        "File", "Directory", "DirectoryInfo", "FileInfo", "Process",
        "ProcessStartInfo", "CultureInfo", "StringInfo", "Enumerable", "Interlocked",
        "ColorConverter", "FontWeights", "FontStyles", "Duration", "SolidColorBrush",
        "LinearGradientBrush", "Key", "MouseButton", "KeyEventArgs", "RoutedEventArgs",
        "DependencyProperty", "TextWrapping", "TextTrimming", "Orientation", "Stretch",
        "LineJoin", "PenLineCap", "DoubleAnimation", "ThicknessAnimation", "CubicEase",
        "EasingMode", "StringComparison", "Uri", "Encoding", "Bitmap", "BitmapImage",
        "Cursors", "Points", "PixelFormats", "RenderTargetBitmap", "PngBitmapEncoder",
        "Screen", "Rectangle", "Graphics", "Bitmap", "EventArgs", "Console",
    }

    for xf in sorted(ROOT.glob("*.xaml")):
        xaml = xf.read_text(encoding="utf-8")
        try:
            ET.fromstring(xaml)
        except ET.ParseError as e:
            errors.append(f"{xf.name}: 不是合法 XML —— {e}")

        for ref in set(re.findall(r'local:MdTheme\.(\w+)', xaml)):
            if ref not in members:
                errors.append(f"{xf.name}: 引用了不存在的 MdTheme.{ref}")
        # 嵌套类型在 x:Static 里不可用 —— 这是最隐蔽的一类坑
        for nested in set(re.findall(r'local:MdTheme\.(Type|Shape)\.', xaml)):
            errors.append(
                f"{xf.name}: x:Static 不能访问嵌套类型 MdTheme.{nested}"
                f"（会报 MC3050），请改用扁平常量")

        cs_name = xf.with_suffix(".xaml.cs")
        if cs_name.exists():
            cs = cs_name.read_text(encoding="utf-8")
            names = set(re.findall(r'x:Name="(\w+)"', xaml))
            used = set(re.findall(
                r'(?<![\w."])([A-Z]\w+)\.(?:Children|Visibility|Text|IsChecked|Items'
                r'|Content|SelectedIndex|ItemsSource|Background|Foreground)', cs))
            for u in sorted(used - names - NOT_A_CONTROL):
                errors.append(f"{cs_name.name}: 用到控件 {u}，但 {xf.name} 里没有 x:Name=\"{u}\"")
            handlers = set(re.findall(
                r'(?:Click|KeyDown|KeyUp|SelectionChanged|Checked|Unchecked|'
                r'TextChanged|MouseDown)="(\w+)"', xaml))
            for h in sorted(handlers):
                if not re.search(r'\bvoid\s+' + h + r'\s*\(', cs):
                    errors.append(f"{cs_name.name}: 事件 {h} 没有实现")

    notes.append(f"MdTheme 公开成员 {len(members)} 个｜Type 档位 {len(type_members)}｜Shape 档位 {len(shape_members)}")

    for n in notes:
        print("  " + n)
    if errors:
        print(f"\n发现 {len(errors)} 个问题：")
        for e in errors:
            print("  ✗ " + e)
        return 1
    print("  ✓ 静态引用检查全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
