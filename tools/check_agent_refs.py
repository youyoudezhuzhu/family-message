#!/usr/bin/env python3
"""Windows Agent 的静态引用检查 —— 在编译前跑，提前抓出「运行期才会炸」的错误。

为什么需要它：本地没有 .NET 工具链，改 C# 只能靠云端编译验证，一轮往返好几分钟；
而更麻烦的是**有些错误编译能过、运行时才炸**。下面这条就是典型（现已随 WPF 手绘界面
一起删除，但规则留着，将来再写 XAML 一样会踩）：

    System.InvalidCastException: Specified cast is not valid.
       at System.Windows.Controls.Border.get_CornerRadius()

根因是 {x:Static} 把一个 double 常量塞给了 Border.CornerRadius（类型是
CornerRadius 结构）—— x:Static 的返回值**不走类型转换**，WPF 原样存进去，
直到布局阶段拆箱才炸。异常点在 Show() 里，现象是「窗口永远打不开」，
跟属性本身看起来毫无关系，花了三轮发布才定位。

★ 注：本文件里的正则**刻意不使用反斜杠转义**（用 [A-Za-z0-9_] 代替 \\w、
用 [.] 代替 \\.），因为这类脚本很容易在多层转义中被写成双反斜杠而悄悄失效。

检查项：
  1. .cs 花括号平衡
  2. using 覆盖（本项目 ImplicitUsings=disable）
  3. XAML 是合法 XML
  4. x:Static 引用的成员必须真的存在（本目录所有 .cs 里的 public/internal static 成员）
  5. ★ x:Static 的常量类型必须匹配目标属性（防止 InvalidCastException）
  6. x:Static 不能访问嵌套类型（MC3050）
  7. XAML 的 x:Name 控件、事件处理函数都有对应实现
  8. ★ 跨类调用缺少类名前缀（CS0103）
  9. 已删除的手绘 UI（PopupWindow / MdTheme / MessageCard / MdPalette）不能有残留引用
 10. csproj 必须引 WebView2（固定版本）并把 web/shell 的页面复制到输出目录

变更记录：
  2026-09-24 PC 端改为 WebView2 壳：删掉 MdTheme 成员/Type/Shape/DynamicResource 那几项
  （MdTheme.cs 已删除，留着只会报假错），第 4/5 项改成不依赖具体某个类的通用规则；
  新增第 9/10 项，防止手绘 UI 或被删的文件又悄悄回来。

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

# 已经删掉的手绘 UI —— 这些名字再出现就是没删干净（注释里不算）
REMOVED_UI = ("PopupWindow", "MdTheme", "MessageCard", "MdPalette")

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
    "AgentLog", "PowerControl", "AutoStart", "ScreenCapture",
}

errors: list[str] = []

# public/internal [static] [readonly|const] 类型 名字
_STATIC_DECL = re.compile(
    r"^(?:public|internal)[ \t]+(?:static[ \t]+)?(?:readonly[ \t]+|const[ \t]+)?"
    r"([A-Za-z_][A-Za-z0-9_<>,\[\]\.\?]*)[ \t]+([A-Za-z_][A-Za-z0-9_]*)"
    r"[ \t]*(?:=>|=|;|\{|\()"
)
_CLASS_DECL = re.compile(
    r"(?:(?:public|private|protected|internal|static|sealed|abstract|partial)[ \t]+)*"
    r"(?:class|struct|record|interface)[ \t]+([A-Za-z_][A-Za-z0-9_]*)"
)


def strip_comments(src: str) -> str:
    """去掉注释与字符串字面量内容（跨类调用 / 残留检查都只看真代码）。"""
    src = re.sub(r"/\*.*?\*/", " ", src, flags=re.S)
    src = re.sub(r"//[^\n]*", " ", src)
    src = re.sub(r'@"(?:[^"]|"")*"', '""', src, flags=re.S)
    src = re.sub(r'\$?"(?:\\.|[^"\\])*"', '""', src)
    return src


def collect_static_members() -> dict:
    """本目录所有 .cs 里的 public/internal 静态成员 → {(类名, 成员名): 类型}。

    只用于 x:Static 检查（XAML 只能引用公开的静态成员）。
    """
    members: dict = {}
    for f in sorted(ROOT.glob("*.cs")):
        text = strip_comments(f.read_text(encoding="utf-8", errors="replace"))
        cur_class = ""
        for raw in text.splitlines():
            line = raw.strip()
            if not line:
                continue
            m = _CLASS_DECL.match(line)
            if m:
                cur_class = m.group(1)
                continue
            m = _STATIC_DECL.match(line)
            if m:
                type_name, name = m.group(1), m.group(2)
                if name in ("var", "int", "string", "bool", "double", "void", "object"):
                    continue
                members.setdefault((cur_class, name), type_name)
    return members


def main() -> int:
    if not ROOT.exists():
        print(f"找不到 {ROOT}")
        return 1

    cs_files = sorted(ROOT.glob("*.cs"))
    xaml_files = sorted(ROOT.glob("*.xaml"))
    if not cs_files:
        print(f"{ROOT} 下没有 .cs 文件")
        return 1

    static_members = collect_static_members()

    # ── ① .cs：花括号平衡 + using 覆盖 ──
    for f in cs_files:
        txt = f.read_text(encoding="utf-8", errors="replace")
        if txt.count("{") != txt.count("}"):
            errors.append(f"{f.name}: 花括号不平衡 {txt.count('{')}/{txt.count('}')}")

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

    # ── ② XAML 检查 ──
    for xf in xaml_files:
        xaml = xf.read_text(encoding="utf-8", errors="replace")
        try:
            ET.fromstring(xaml)
        except ET.ParseError as e:
            errors.append(f"{xf.name}: 不是合法 XML —— {e}")

        # ④ x:Static 引用的成员必须存在（重建 MdTheme 时代的等价规则，但不再绑定某个类）
        for cls, member in set(re.findall(
                r"x:Static[ ]+local:(" + IDENT + r")[.](" + IDENT + r")[}]", xaml)):
            if (cls, member) not in static_members:
                errors.append(f"{xf.name}: x:Static local:{cls}.{member} 找不到对应的"
                              f" public/internal 静态成员（该名字被改名或删除了？）")

        # ⑤ ★ 类型匹配：编译能过，但运行期会 InvalidCastException
        # 注意属性值外面有引号：CornerRadius="{x:Static local:MdTheme.RadiusFull}"
        static_re = (r"(" + IDENT + r")" + WS + r'=[ \t]*"[{]x:Static local:'
                     + r"(" + IDENT + r")[.](" + IDENT + r")[}][\"]")
        for prop, cls, member in re.findall(static_re, xaml):
            want = PROP_TYPES.get(prop)
            got = static_members.get((cls, member))
            if want and got and got != want:
                errors.append(
                    f"{xf.name}: {prop}={{x:Static local:{cls}.{member}}} 类型不匹配 —— "
                    f"{prop} 需要 {want}，而 {member} 声明成了 {got}"
                    f"（x:Static 不做类型转换，运行期会抛 InvalidCastException）")

        # ⑥ x:Static 不能访问嵌套类型（MC3050）
        for cls, nested in set(re.findall(
                r"x:Static[ ]+local:(" + IDENT + r")[.](" + IDENT + r")[.]", xaml)):
            errors.append(f"{xf.name}: x:Static 不能访问嵌套类型 {cls}.{nested}"
                          f"（会报 MC3050），请改用扁平常量")

        # ⑦ 控件名 / 事件处理函数
        cs_name = xf.with_suffix(".xaml.cs")
        if cs_name.exists():
            cs = cs_name.read_text(encoding="utf-8", errors="replace")
            names = set(re.findall(r'x:Name="(' + IDENT + r')"', xaml))
            # ⚠ 原本这里是 ([A-Z] + IDENT[1:])，靠切掉 IDENT 的首字符拼出类名 —— 拼出来的
            #   模式是坏的（[A-Z]A-Za-z_]...），这个检查其实**从来没生效过**。改成直白的
            #   大写开头标识符，别再用字符串拼接取巧。
            used = set(re.findall(
                r'(?<![A-Za-z0-9_."])([A-Z][A-Za-z0-9_]*)[.](?:Children|Visibility|Text'
                r'|IsChecked|Items|Content|SelectedIndex|ItemsSource|Background|Foreground)', cs))
            for u in sorted(used - names - NOT_A_CONTROL):
                errors.append(f"{cs_name.name}: 用到控件 {u}，但 {xf.name} 里没有 x:Name=\"{u}\"")
            handlers = set(re.findall(
                r'(?:Click|KeyDown|KeyUp|SelectionChanged|Checked|Unchecked'
                r'|TextChanged|MouseDown|RequestNavigate)="(' + IDENT + r')"', xaml))
            for h in sorted(handlers):
                if not re.search(r"void[ \t]+" + h + r"[ \t]*[(]", cs):
                    errors.append(f"{cs_name.name}: 事件 {h} 没有实现")

    # ── ③ 已删除的手绘 UI 不能有残留引用 ──
    for f in sorted(list(ROOT.glob("*.cs")) + list(ROOT.glob("*.xaml"))):
        text = f.read_text(encoding="utf-8", errors="replace")
        if f.suffix == ".xaml":
            # XAML 注释 <!-- --> 里的说明不算引用
            text = re.sub(r"<!--.*?-->", " ", text, flags=re.S)
        else:
            text = strip_comments(text)
        for token in REMOVED_UI:
            if token in text:
                errors.append(f"{f.name}: 还引用着已删除的 {token}（PC 端界面已改为 WebView2 壳，"
                              f"见 docs/PC-WEBVIEW2-REWRITE.md）")

    # ── csproj：WebView2 包 + 本地页面随 exe 发布 ──
    csproj = ROOT / "FamilyAgent.csproj"
    if csproj.exists():
        proj = csproj.read_text(encoding="utf-8", errors="replace")
        m = re.search(r'PackageReference[ \t]+Include="Microsoft[.]Web[.]WebView2"[ \t]+Version="([^"]+)"', proj)
        if not m:
            errors.append("FamilyAgent.csproj: 少了 Microsoft.Web.WebView2 包引用（壳模式必需）")
        elif "*" in m.group(1):
            errors.append("FamilyAgent.csproj: WebView2 用了浮动版本 "
                          f"{m.group(1)}，云编译不可复现，请写死版本号")
        if not re.search(r"Content[ \t]+Include=" + r'"[^"]*web\\shell[^"]*"', proj):
            errors.append("FamilyAgent.csproj: 没把 web/shell 的本地页面复制到输出目录 "
                          "（boot/offline 页会缺失）")
        elif "CopyToOutputDirectory" not in proj:
            errors.append("FamilyAgent.csproj: shell 的 Content 没有 CopyToOutputDirectory"
                          "（会只列不复制）")
    else:
        errors.append("找不到 FamilyAgent.csproj")

    print(f"  静态成员 {len(static_members)} 个｜.cs {len(cs_files)} 个｜.xaml {len(xaml_files)} 个")
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


# ═══════════════════════════════════════════════════════════════════
#  跨类调用检查（CS0103）
#
#  本机没有 .NET，无法编译，所以任何一句 C# 语义错误都要等云端编译
#  跑完才知道 —— 一轮 3 分钟，而且编译器的报错位置离真正原因很远。
#  实际踩过的例子：
#      error CS0103: The name 'ToFluent' does not exist in the current context
#  原因只是 ToFluent 定义在 App 里，从别的类调用时漏了 App. 前缀。
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
        decls_by_file[f] = _file_decls(strip_comments(
            f.read_text(encoding="utf-8", errors="replace")))

    # 某个名字被哪些「文件」定义过（同文件内多类一律放过，避免嵌套类误报）
    owners = {}
    for f, decls in decls_by_file.items():
        for name in decls:
            owners.setdefault(name, set()).add(f)

    bad = []
    for f in files:
        mine = set(decls_by_file[f])
        src = strip_comments(f.read_text(encoding="utf-8", errors="replace"))
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
