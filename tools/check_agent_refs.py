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

    # ── 跨类调用检查（防 CS0103，省一轮云端编译）──
    if check_cross_class_calls() != 0:
        return 1

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

# ═══════════════════════════════════════════════════════════════════
#  跨类调用检查（CS0103）
#
#  本机没有 .NET，无法编译，所以任何一句 C# 语义错误都要等云端编译
#  跑完才知道 —— 一轮 3 分钟，而且编译器的报错位置离真正原因很远。
#  实际踩过的例子：
#      error CS0103: The name 'ToFluent' does not exist in the current context
#  原因只是 ToFluent 定义在 App 里，从 PopupWindow 调用时漏了 App. 前缀。
#
#  规则：在文件 F 里看到一次「不加限定的调用 Ident(...)」，而 Ident 只被
#  其它文件的类定义过 —— 那就是编译错误。
#
#  为了不误报，只在「F 里没有任何类定义过 Ident」时才报；也就是同文件
#  内多类/嵌套类的情况一律放过。
# ═══════════════════════════════════════════════════════════════════

_ID = r"[A-Za-z_][A-Za-z0-9_]*"

# 语言关键字和伪函数，后面跟 "(" 但不是方法调用
_NOT_A_CALL = {
    "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return",
    "new", "nameof", "typeof", "sizeof", "default", "checked", "unchecked", "fixed",
    "while", "do", "else", "get", "set", "throw", "await", "yield", "case", "when",
    "stackalloc", "in", "out", "ref", "params", "is", "as", "this", "base",
}


def _strip_comments(src: str) -> str:
    src = re.sub(r"/\*.*?\*/", " ", src, flags=re.S)
    src = re.sub(r"//[^\n]*", " ", src)
    # 字符串字面量里的内容不算代码
    src = re.sub(r'@"(?:[^"]|"")*"', '""', src, flags=re.S)
    src = re.sub(r'\$?"(?:\\.|[^"\\])*"', '""', src)
    return src


# C# 关键字/内建类型名，不能当成员名
_CS_KEYWORDS = {
    "var", "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort",
    "float", "double", "decimal", "bool", "char", "string", "object", "void",
    "dynamic", "nint", "nuint", "when", "else", "try", "finally", "catch",
    "get", "set", "add", "remove", "init", "record", "struct", "class", "enum",
    "interface", "namespace", "using", "lock", "fixed", "checked", "unchecked",
}


def _file_decls(src: str) -> dict:
    """提取「本文件定义过的成员 → 所属类名」。

    只用作白名单，所以宁可多收不漏收；但关键字必须排除，
    否则 `var x = ...` 会让 var 变成一个「成员」，到处误报。
    """
    decls: dict = {}
    cur_class = ""
    for line in src.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue

        # 类/结构体声明，用于把成员归属到正确的类
        m = re.match(
            r"(?:(?:public|private|protected|internal|static|sealed|abstract|partial)\s+)*"
            r"(?:class|struct|record|interface)\s+(" + _ID + r")", line)
        if m:
            cur_class = m.group(1)
            continue

        # 方法声明：修饰符? 返回类型 名字(
        m = re.match(
            r"(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|virtual|"
            r"override|async|sealed|partial|new|unsafe|extern|readonly)\s+)*"
            r"[\w<>\[\],\.\?]+\s+(" + _ID + r")\s*[(<]", line)
        if m:
            name = m.group(1)
            if name not in _CS_KEYWORDS:
                decls[name] = cur_class
            continue

        # 构造函数：类名(
        m = re.match(r"(?:(?:public|private|protected|internal|static)\s+)?(" + _ID + r")\s*\(", line)
        if m:
            name = m.group(1)
            if name == cur_class or (name and name[0].isupper() and name not in _CS_KEYWORDS):
                decls[name] = cur_class
            continue

        # 本地函数 / 委托赋值：名字 = ( 或 名字 => 
        m = re.search(r"\b(" + _ID + r")\s*=\s*(?:\([^)]*\)\s*=>|delegate|async)", line)
        if m and m.group(1) not in _CS_KEYWORDS:
            decls[m.group(1)] = cur_class
    return decls


def check_cross_class_calls() -> int:
    proj = Path(__file__).resolve().parent.parent / "pc-agent" / "FamilyAgent"
    files = sorted(proj.glob("*.cs"))
    if not files:
        print("  (跳过跨类调用检查：找不到 .cs 文件)")
        return 0

    decls_by_file = {}
    for f in files:
        decls_by_file[f] = _file_decls(_strip_comments(
            f.read_text(encoding="utf-8", errors="replace")))

    # 某个名字被哪些「文件」定义过（同文件内多类一律放过，避免嵌套类误报）
    owners = {}
    for f, decls in decls_by_file.items():
        for name in decls:
            owners.setdefault(name, set()).add(f)

    bad = []
    for f in files:
        mine = set(decls_by_file[f])
        src = _strip_comments(f.read_text(encoding="utf-8", errors="replace"))
        # 收集本文件的局部变量/参数名，避免把变量当方法
        locals_ = set(re.findall(r"\b(?:var|" + _ID + r"(?:<[^>]*>)?)\s+(" + _ID + r")\s*=", src))
        locals_ |= set(re.findall(r"\b(" + _ID + r")\s*=>", src))
        for i, line in enumerate(src.splitlines(), 1):
            for m in re.finditer(r"(?<![.\w])(" + _ID + r")\s*\(", line):
                name = m.group(1)
                if name in _NOT_A_CALL or name in mine or name in locals_:
                    continue
                # `new Xxx(` 是构造函数调用，不是漏了类名前缀
                if re.search(r"\bnew\s+$", line[:m.start()]):
                    continue
                others = owners.get(name, set()) - {f}
                if others:
                    cls = next((decls_by_file[o].get(name) for o in others
                                if decls_by_file[o].get(name)), "")
                    bad.append((f.name, i, name, sorted(x.name for x in others), cls))

    if bad:
        print("\n  ✗ 跨类调用缺少类名前缀（C# 会报 CS0103）：")
        seen = set()
        for fname, ln, name, where, cls in bad:
            key = (fname, name)
            if key in seen:
                continue
            seen.add(key)
            print(f"      {fname}:{ln}  {name}(...)  ←  定义在 {', '.join(where)}")
            hint = f"{cls}.{name}(...)" if cls else "加类名前缀"
            print(f"          应写成 {hint}")
        return 1
    print("  ✓ 跨类调用检查通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
