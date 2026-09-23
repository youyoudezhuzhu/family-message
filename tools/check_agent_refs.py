#!/usr/bin/env python3
"""Windows Agent 的静态引用检查 —— 在编译前跑，提前抓出「运行期才会炸」的错误。

为什么需要它：本地没有 .NET 工具链，改 C# 只能靠云端编译验证，一轮往返好几分钟；
而更麻烦的是**有些错误编译能过、运行时才炸**。下面这条就是典型：

    System.InvalidCastException: Specified cast is not valid.
       at System.Windows.Controls.Border.get_CornerRadius()
       at FamilyAgent.PopupWindow.EnsureShown()

根因是 {x:Static} 把一个 double 常量塞给了 Border.CornerRadius（类型是
CornerRadius 结构）—— x:Static 的返回值**不走类型转换**，WPF 原样存进去，
直到布局阶段拆箱才炸。异常点在 Show() 里，现象是「窗口永远打不开」，
跟属性本身看起来毫无关系，花了三轮发布才定位。

★ 注：本文件里的正则**刻意不使用反斜杠转义**（用 [A-Za-z0-9_] 代替 \\w、
用 [.] 代替 \\.），因为这类脚本很容易在多层转义中被写成双反斜杠而悄悄失效。

检查项：
  1. .cs 里 MdTheme.X / MdTheme.Type.X / MdTheme.Shape.X 引用都存在
  2. .xaml 里 local:MdTheme.X 引用都存在
  3. XAML 的 x:Static 不支持嵌套类型（MC3050）
  4. ★ x:Static 的常量类型必须匹配目标属性（防止 InvalidCastException）
  5. XAML 用的 DynamicResource 键都已在 MdTheme 中定义
  6. XAML 的 x:Name 控件、事件处理函数都有对应实现
  7. using 覆盖（本项目 ImplicitUsings=disable）
  8. 花括号平衡 / XAML 是合法 XML

用法：python3 tools/check_agent_refs.py [pc-agent/FamilyAgent]
退出码 0 = 全通过，1 = 有问题。
"""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

# Windows 上 CI 的 stdout 默认是 cp1252/GBK，直接 print 中文会 UnicodeEncodeError，
# 把检查结果本身变成失败原因 —— 强制切到 UTF-8。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = Path(sys.argv[1] if len(sys.argv) > 1 else "pc-agent/FamilyAgent")

# 反斜杠免疫的通用片段
IDENT = r"[A-Za-z_][A-Za-z0-9_]*"          # 标识符
WS = r"[ \t]*"                              # 空白
QUAL = IDENT + r"(?:[.]" + IDENT + r")*"    # 限定名 Foo.Bar.Baz

# XAML 里 {x:Static} 的常量类型必须和目标属性一致，否则运行期拆箱失败
PROP_TYPES = {
    "CornerRadius": "CornerRadius",
    "FontSize": "double",
    "StrokeThickness": "double",
    "Opacity": "double",
    "Margin": "Thickness",
    "Padding": "Thickness",
    "BorderThickness": "Thickness",
}

# 用到这些类型时必须有对应 using
NEEDS = [
    ("Dictionary<", "System.Collections.Generic"),
    ("List<", "System.Collections.Generic"),
    ("IReadOnlyList<", "System.Collections.Generic"),
    ("SolidColorBrush", "System.Windows.Media"),
    ("ColorConverter", "System.Windows.Media"),
    ("Registry", "Microsoft.Win32"),
    (".Select(", "System.Linq"),
    (".Where(", "System.Linq"),
    (".FirstOrDefault(", "System.Linq"),
    ("StringInfo", "System.Globalization"),
]

errors: list[str] = []


def main() -> int:
    theme_path = ROOT / "MdTheme.cs"
    if not theme_path.exists():
        print(f"找不到 {theme_path}")
        return 1
    theme = theme_path.read_text(encoding="utf-8")

    # ── 收集 MdTheme 的公开成员 + 各自的声明类型 ──
    # 逐行解析，不用一个大正则：正则在类型名/泛型上很容易贪婪跑偏，
    # 而且一旦写错就是「悄悄少认几个成员」，反而制造出假的「成员不存在」。
    members: set[str] = set()
    declared: dict[str, str] = {}

    for raw in theme.splitlines():
        line = raw.strip()
        if not line.startswith("public "):
            continue

        rest = line[len("public "):]
        # 名称 = 等号 / 分号 / 大括号 / 左括号 / 箭头 之前的最后一个标识符
        cut = len(rest)
        for sep in ("=", ";", "{", "(", "=>"):
            i = rest.find(sep)
            if i >= 0:
                cut = min(cut, i)
        head = rest[:cut].strip()
        if not head:
            continue

        # 形如 "static readonly CornerRadius RadiusFull" → 最后一个标识符是名字
        ids = re.findall(r"[A-Za-z_][A-Za-z0-9_]*", head)
        if not ids:
            continue
        name = ids[-1]
        members.add(name)

        # 类型（倒数第二个标识符，尽力而为即可 —— 只用于类型匹配检查）
        skip = {"static", "readonly", "const", "sealed", "override", "virtual",
                "abstract", "partial", "async", "record", "class", "struct", "enum"}
        types = [i for i in ids[:-1] if i not in skip]
        if types:
            declared.setdefault(name, types[-1])

    # 嵌套类
    for m in re.finditer(r"public[ \t]+static[ \t]+class[ \t]+([A-Za-z_][A-Za-z0-9_]*)", theme):
        members.add(m.group(1))

    # Type / Shape 里的常量
    type_block = theme.split("public static class Type")[1].split("public static class Shape")[0]
    shape_block = theme.split("public static class Shape")[1].split("// ── 给 XAML 用")[0]
    type_members = set(re.findall(r"public[ \t]+const[ \t]+double[ \t]+(" + IDENT + r")", type_block))
    shape_members = set(re.findall(r"public[ \t]+const[ \t]+double[ \t]+(" + IDENT + r")", shape_block))

    # ── ① .cs 检查 ──
    for f in sorted(ROOT.glob("*.cs")):
        txt = f.read_text(encoding="utf-8")

        for m in set(re.findall(r"(?<![A-Za-z0-9_.])MdTheme[.](" + IDENT + r")", txt)):
            if m not in members and m not in ("Type", "Shape"):
                errors.append(f"{f.name}: 引用了不存在的 MdTheme.{m}")
        for m in set(re.findall(r"MdTheme[.]Type[.](" + IDENT + r")", txt)):
            if m not in type_members:
                errors.append(f"{f.name}: 引用了不存在的 MdTheme.Type.{m}")
        for m in set(re.findall(r"MdTheme[.]Shape[.](" + IDENT + r")", txt)):
            if m not in shape_members:
                errors.append(f"{f.name}: 引用了不存在的 MdTheme.Shape.{m}")

        lines = txt.splitlines()
        usings = {
            re.sub(r"^[ \t]*using[ \t]+(?:static[ \t]+)?|;[ \t]*$", "", ln)
            for ln in lines if ln.strip().startswith("using ")
        }
        body = "\n".join(ln for ln in lines if not ln.strip().startswith("using "))
        for token, ns in NEEDS:
            if ns in usings:
                continue
            # 只看「未限定」用法：System.IO.Path. 这种全限定名不需要 using，
            # 而且是刻意为之（避免和 WPF 的 System.Windows.Shapes.Path 撞名）
            if not re.search(r"(?<![A-Za-z0-9_.])" + re.escape(token), body):
                continue
            errors.append(f"{f.name}: 用到 {token} 但缺 using {ns}")

        if txt.count("{") != txt.count("}"):
            errors.append(f"{f.name}: 花括号不平衡 {txt.count('{')}/{txt.count('}')}")

    # ── ② XAML 检查 ──
    for xf in sorted(ROOT.glob("*.xaml")):
        xaml = xf.read_text(encoding="utf-8")
        try:
            ET.fromstring(xaml)
        except ET.ParseError as e:
            errors.append(f"{xf.name}: 不是合法 XML —— {e}")

        for ref in set(re.findall(r"local:MdTheme[.](" + IDENT + r")", xaml)):
            if ref not in members:
                errors.append(f"{xf.name}: 引用了不存在的 MdTheme.{ref}")

        # ③ x:Static 不能访问嵌套类型
        for nested in set(re.findall(r"local:MdTheme[.](Type|Shape)[.]", xaml)):
            errors.append(f"{xf.name}: x:Static 不能访问嵌套类型 MdTheme.{nested}"
                          f"（会报 MC3050），请改用扁平常量")

        # ④ ★ 类型匹配：编译能过，但运行期会 InvalidCastException
        # 注意属性值外面有引号：CornerRadius="{x:Static local:MdTheme.RadiusFull}"
        static_re = (r"(" + IDENT + r")" + WS + r'=[ \t]*"[{]x:Static local:MdTheme[.]'
                     + r"(" + IDENT + r")[}][\"]")
        for prop, member in re.findall(static_re, xaml):
            want = PROP_TYPES.get(prop)
            got = declared.get(member)
            if want and got and got != want:
                errors.append(
                    f"{xf.name}: {prop}={{x:Static MdTheme.{member}}} 类型不匹配 —— "
                    f"{prop} 需要 {want}，而 {member} 声明成了 {got}"
                    f"（x:Static 不做类型转换，运行期会抛 InvalidCastException）")

        # ⑤ DynamicResource 键必须存在
        defined = set(re.findall(r'[(]"(Md' + IDENT + r')"', theme))
        defined |= set(re.findall(r'map\["(Md' + IDENT + r')"\]', theme))
        defined |= set(re.findall(r'\["(Md' + IDENT + r')"\][ \t]*=', theme))
        for key in set(re.findall(r"[{]DynamicResource[ ]+(" + IDENT + r")[}]", xaml)):
            if key not in defined:
                errors.append(f"{xf.name}: DynamicResource {key} 在 MdTheme 里没有定义")

        # ⑥ 控件名 / 事件处理函数
        cs_name = xf.with_suffix(".xaml.cs")
        if cs_name.exists():
            cs = cs_name.read_text(encoding="utf-8")
            names = set(re.findall(r'x:Name="(' + IDENT + r')"', xaml))
            used = set(re.findall(
                r"(?<![A-Za-z0-9_.\"])([A-Z]" + IDENT[1:] + r")[.](?:Children|Visibility|Text"
                r"|IsChecked|Items|Content|SelectedIndex|ItemsSource|Background|Foreground)", cs))
            for u in sorted(used - names - NOT_A_CONTROL):
                errors.append(f"{cs_name.name}: 用到控件 {u}，但 {xf.name} 里没有 x:Name=\"{u}\"")
            handlers = set(re.findall(
                r'(?:Click|KeyDown|KeyUp|SelectionChanged|Checked|Unchecked'
                r'|TextChanged|MouseDown)="(' + IDENT + r')"', xaml))
            for h in sorted(handlers):
                if not re.search(r"void[ \t]+" + h + r"[ \t]*[(]", cs):
                    errors.append(f"{cs_name.name}: 事件 {h} 没有实现")

    print(f"  MdTheme 公开成员 {len(members)} 个｜Type 档位 {len(type_members)}｜"
          f"Shape 档位 {len(shape_members)}｜已识别类型 {len(declared)} 个")
    if errors:
        print(f"\n发现 {len(errors)} 个问题：")
        for e in errors:
            print("  ✗ " + e)
        return 1
    print("  ✓ 静态引用检查全部通过")
    return 0


# 控件名检查的白名单：这些是 .NET 类型/命名空间，也会以 X.Something 出现，但不是 x:Name
NOT_A_CONTROL = {
    "System", "Microsoft", "Windows", "Forms", "Media", "Input", "Threading",
    "Tasks", "Diagnostics", "IO", "Text", "Json", "Collections", "Generic",
    "Linq", "Globalization", "Net", "Http", "Drawing", "Security", "Runtime",
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
    "Screen", "Rectangle", "Graphics", "EventArgs", "Console", "CornerRadius",
    "AgentLog", "PowerControl", "AutoStart", "ScreenCapture", "MdTheme", "MdPalette",
}

if __name__ == "__main__":
    sys.exit(main())
