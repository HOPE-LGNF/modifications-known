"""Modifications Known — 应用图标生成器（像素风，16/32 双母版）。

设计取自启动器自身的三套主题（ThemePalettes）与《未知伤亡》的美术语言：
  - 深色底 + 单一亮色主体，对应游戏的"暗部 + 光源"构图
  - 薄荷绿（moss accent #79C99E）作为主色，三套主题都能相邻成立
  - 芯片 = 模组（游戏里 "未植入芯片" 即无 HUD 模式）
  - 勾 = 已知（Known），与"未知伤亡"的 Unknown 构成反义
  - 琥珀带（warning #E5B567）= 测试版标识

输出：
  icon-test.ico / icon-release.ico   （16/24/32/48/64/128/256）
  icon-*-{size}.png                  单尺寸 PNG，便于 Avalonia 窗口图标
"""

from PIL import Image, ImageDraw

# —— 调色板（moss 主题）———————————————————————————————
BG = (15, 20, 18, 255)         # #0F1412
BORDER = (42, 55, 48, 255)     # #2A3730
ACCENT = (121, 201, 158, 255)  # #79C99E
ACCENT_DARK = (88, 150, 116, 255)
INK = (20, 30, 26, 255)        # 勾与文字（深底色的近黑）
AMBER = (229, 181, 103, 255)   # #E5B567

FONT = {
    "T": ["111", "010", "010", "010", "010"],
    "E": ["111", "100", "110", "100", "111"],
    "S": ["111", "100", "111", "001", "111"],
}


def draw_text(d, text, x, y, color):
    for i, ch in enumerate(text):
        glyph = FONT[ch]
        ox = x + i * 4
        for gy, row in enumerate(glyph):
            for gx, bit in enumerate(row):
                if bit == "1":
                    d.rectangle((ox + gx, y + gy, ox + gx, y + gy), fill=color)


def clip_round(im, size, radius):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    im.putalpha(mask)
    ImageDraw.Draw(im).rounded_rectangle(
        (0, 0, size - 1, size - 1), radius=radius, outline=BORDER, width=1
    )


def make_32(beta: bool) -> Image.Image:
    S = 32
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((0, 0, S - 1, S - 1), radius=6, fill=BG)

    # 芯片引脚（左侧/右侧，短而暗）
    for y in (9, 14, 19):
        d.rectangle((1, y, 3, y), fill=ACCENT_DARK)
        d.rectangle((28, y, 30, y), fill=ACCENT_DARK)

    # 芯片本体：薄荷底
    d.rounded_rectangle((4, 3, 27, 23), radius=5, fill=ACCENT, outline=ACCENT_DARK)

    # 勾（深色，3px）
    d.line([(9, 13), (14, 18), (22, 8)], fill=INK, width=3, joint="curve")

    # 测试版：底部琥珀带 + TEST
    if beta:
        d.rectangle((0, 25, S - 1, S - 1), fill=AMBER)
        draw_text(d, "TEST", 8, 26, INK)

    clip_round(im, S, 6)
    return im


def make_16(beta: bool) -> Image.Image:
    """16px 手绘母版：去掉引脚与文字，只保留芯片 + 勾 + 色带。"""
    S = 16
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((0, 0, S - 1, S - 1), radius=3, fill=BG)

    bottom = 11 if beta else 13
    d.rounded_rectangle((2, 1, 13, bottom), radius=3, fill=ACCENT, outline=ACCENT_DARK)

    # 勾：短下笔 + 长上笔（2px）
    d.line([(5, 6), (7, 8)], fill=INK, width=2)
    d.line([(7, 8), (11, 4)], fill=INK, width=2)
    d.rectangle((7, 8, 7, 8), fill=INK)

    if beta:
        d.rectangle((0, 13, S - 1, S - 1), fill=AMBER)

    clip_round(im, S, 3)
    return im


def upscale(im, size):
    if size == im.width:
        return im
    if size % im.width == 0:
        return im.resize((size, size), Image.NEAREST)
    return im.resize((size, size), Image.LANCZOS)


def main():
    import os

    out = os.path.dirname(os.path.abspath(__file__))
    sizes = [16, 24, 32, 48, 64, 128, 256]

    for name, beta in (("icon-test", True), ("icon-release", False)):
        m32 = make_32(beta)
        m16 = make_16(beta)

        frames = []
        for s in sizes:
            src = m16 if s <= 16 else m32
            frames.append(upscale(src, s))
            upscale(src, s).save(os.path.join(out, f"{name}-{s}.png"))

        frames[-1].save(
            os.path.join(out, f"{name}.ico"),
            format="ICO",
            sizes=[(s, s) for s in sizes],
            append_images=frames[:-1],
        )

    print("done")


if __name__ == "__main__":
    main()
