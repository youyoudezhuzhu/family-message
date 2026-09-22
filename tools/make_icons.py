#!/usr/bin/env python3
"""生成应用图标：圆角渐变底 + 房子 + 对话气泡。输出 256 / 64 两种尺寸。"""
from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image, ImageDraw

OUT_DIRS = [Path(p) for p in sys.argv[1:]] or [Path("fpk")]

SIZE = 256


def rounded_bg(size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    grad = Image.new("RGBA", (size, size))
    d = ImageDraw.Draw(grad)
    top = (58, 150, 226)
    bot = (32, 96, 176)
    for y in range(size):
        t = y / max(size - 1, 1)
        d.line(
            [(0, y), (size, y)],
            fill=(
                int(top[0] + (bot[0] - top[0]) * t),
                int(top[1] + (bot[1] - top[1]) * t),
                int(top[2] + (bot[2] - top[2]) * t),
                255,
            ),
        )
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, size - 1, size - 1], radius=int(size * 0.22), fill=255
    )
    img.paste(grad, (0, 0), mask)
    return img


def draw_icon(size: int) -> Image.Image:
    s = size / 256.0
    img = rounded_bg(size)
    d = ImageDraw.Draw(img)
    white = (255, 255, 255, 246)

    # 房子
    d.polygon(
        [(128 * s, 52 * s), (46 * s, 122 * s), (210 * s, 122 * s)], fill=white
    )
    d.rounded_rectangle([70 * s, 118 * s, 186 * s, 196 * s], radius=6 * s, fill=white)

    # 门口
    d.rounded_rectangle(
        [108 * s, 150 * s, 148 * s, 196 * s], radius=4 * s, fill=(45, 105, 175, 255)
    )

    # 对话气泡（消息的象征）
    d.rounded_rectangle(
        [132 * s, 128 * s, 226 * s, 178 * s], radius=14 * s, fill=(250, 205, 85, 255)
    )
    d.polygon(
        [
            (150 * s, 176 * s),
            (140 * s, 200 * s),
            (172 * s, 177 * s),
        ],
        fill=(250, 205, 85, 255),
    )
    for i, x in enumerate((152, 172, 192)):
        d.ellipse(
            [(x - 6) * s, 146 * s, (x + 6) * s, 158 * s],
            fill=(45, 75, 130, 255),
        )
    return img


def main() -> None:
    big = draw_icon(SIZE)
    for out in OUT_DIRS:
        (out / "app" / "ui" / "images").mkdir(parents=True, exist_ok=True)
        big.save(out / "ICON_256.PNG")
        big.resize((64, 64), Image.LANCZOS).save(out / "ICON.PNG")
        big.save(out / "app" / "ui" / "images" / "icon_256.png")
        big.resize((64, 64), Image.LANCZOS).save(out / "app" / "ui" / "images" / "icon_64.png")
        print("icons written to", out)


if __name__ == "__main__":
    main()
