# 프로그램 아이콘(assets/app.ico)을 만든다. 잉크색 육각형 안에 흰 화살표 두 개(바꾸기).
# 실행: python -I tools/make_icon.py
import math
import os
import sys

from PIL import Image, ImageDraw

INK = (23, 24, 28, 255)
WHITE = (255, 255, 255, 255)


def hexagon(cx, cy, r):
    return [(cx + r * math.cos(math.radians(60 * i - 90)), cy + r * math.sin(math.radians(60 * i - 90))) for i in range(6)]


def arrow(draw, x0, x1, y, w, head, color, right=True):
    if right:
        draw.rectangle([x0, y - w / 2, x1 - head * 0.9, y + w / 2], fill=color)
        draw.polygon([(x1, y), (x1 - head, y - head * 0.8), (x1 - head, y + head * 0.8)], fill=color)
    else:
        draw.rectangle([x0 + head * 0.9, y - w / 2, x1, y + w / 2], fill=color)
        draw.polygon([(x0, y), (x0 + head, y - head * 0.8), (x0 + head, y + head * 0.8)], fill=color)


def render(size, simple):
    big = 1024
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    c = big / 2
    d.polygon(hexagon(c, c, big * 0.49), fill=INK)
    if simple:
        arrow(d, big * 0.25, big * 0.75, c, big * 0.13, big * 0.24, WHITE, True)
    else:
        arrow(d, big * 0.26, big * 0.72, big * 0.40, big * 0.085, big * 0.17, WHITE, True)
        arrow(d, big * 0.28, big * 0.74, big * 0.60, big * 0.085, big * 0.17, WHITE, False)
    return img.resize((size, size), Image.LANCZOS)


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_dir = os.path.join(root, "assets")
    os.makedirs(out_dir, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [render(s, s <= 24) for s in sizes]
    ico = os.path.join(out_dir, "app.ico")
    frames[-1].save(ico, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    frames[-1].save(os.path.join(out_dir, "app-256.png"))
    print("saved", ico, os.path.getsize(ico))


if __name__ == "__main__":
    sys.exit(main())
