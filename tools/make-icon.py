#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""贴纸待办（StickerTodo）应用图标生成器。

生成多尺寸 Windows ICO（16/20/24/32/40/48/64/128/256），风格：
  * 蓝色渐变圆角方块（贴纸本体）
  * 白色圆头对勾
  * 右上角折角 + 微光（大尺寸下可见）
  * 每个尺寸独立超采样渲染后用 LANCZOS 缩小，保证 16px 下对勾清晰

用法：
  python tools/make-icon.py                      # 生成到 src/TodoSticker/Assets/app.ico
  python tools/make-icon.py --output <path.ico>  # 自定义输出路径
  python tools/make-icon.py --check-only         # 只校验已有 ICO，不重新生成
  python tools/make-icon.py --preview none       # 不输出预览图（默认 tools/icon-preview.png）

依赖：Pillow（PIL）。缺依赖时退出码 2 并给出中文提示。
自检失败时退出码 1；成功退出码 0。
"""

from __future__ import annotations

import argparse
import io
import struct
import sys
from pathlib import Path

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
ICO_TYPE = 1
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUTPUT = ROOT / "src" / "TodoSticker" / "Assets" / "app.ico"
DEFAULT_PREVIEW = ROOT / "tools" / "icon-preview.png"

# 贴纸配色：顶部天蓝 -> 中部亮蓝 -> 底部深蓝
COLOR_TOP = (118, 202, 255)
COLOR_MID = (44, 137, 240)
COLOR_BOT = (18, 84, 200)
COLOR_EDGE = (10, 62, 148, 205)
COLOR_FLAP = (210, 235, 255, 255)
COLOR_FLAP_EDGE = (255, 255, 255, 175)
COLOR_FOLD_SHADOW = (8, 48, 116, 105)
COLOR_CHECK = (255, 255, 255, 255)
COLOR_CHECK_SHADOW = (4, 44, 112, 110)

_results: list[tuple[bool, str]] = []

# 输出被重定向（如构建脚本/CI 捕获）时统一 UTF-8，避免中文乱码
if hasattr(sys.stdout, "reconfigure") and not sys.stdout.isatty():  # pragma: no cover
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:  # noqa: BLE001
        pass


def report(ok: bool, message: str) -> None:
    _results.append((ok, message))
    print(("  [PASS] " if ok else "  [FAIL] ") + message)


try:
    from PIL import Image, ImageDraw, ImageFilter
except ImportError:  # pragma: no cover
    print("[ERROR] 未找到 Pillow（PIL）。请先安装：python -m pip install Pillow", file=sys.stderr)
    print("        或使用内置 Python 环境运行本脚本。", file=sys.stderr)
    sys.exit(2)


def _resample_lanczos():
    try:
        return Image.Resampling.LANCZOS  # Pillow >= 9.1
    except AttributeError:  # pragma: no cover - 老版本 Pillow 兼容
        return Image.LANCZOS


def _resample_nearest():
    try:
        return Image.Resampling.NEAREST
    except AttributeError:  # pragma: no cover
        return Image.NEAREST


# ---------------------------------------------------------------------------
# 基础绘制工具
# ---------------------------------------------------------------------------
def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def lerp_color(c1, c2, t):
    return tuple(int(round(lerp(c1[i], c2[i], t))) for i in range(3))


def vertical_gradient_strip(height: int, top, mid, bottom):
    """构造 1 x height 的竖直渐变（顶部->中部->底部）。"""
    colors = []
    for y in range(height):
        t = y / max(1, height - 1)
        if t < 0.5:
            c = lerp_color(top, mid, t / 0.5)
        else:
            c = lerp_color(mid, bottom, (t - 0.5) / 0.5)
        colors.append(c + (255,))
    strip = Image.new("RGBA", (1, height))
    strip.putdata(colors)
    return strip


def make_gradient(size: int):
    """二维蓝色渐变：竖直 + 轻微左右明暗变化，让贴纸更有体积感。"""
    strip = vertical_gradient_strip(size, COLOR_TOP, COLOR_MID, COLOR_BOT)
    raw = strip.tobytes()  # RGBA，每像素 4 字节
    darker = Image.new("RGBA", (1, size))
    darker.putdata(
        [
            (max(0, raw[i] - 12), max(0, raw[i + 1] - 10), max(0, raw[i + 2] - 8), 255)
            for i in range(0, len(raw), 4)
        ]
    )
    cols = Image.new("RGBA", (2, size))
    cols.paste(strip, (0, 0))
    cols.paste(darker, (1, 0))
    return cols.resize((size, size), Image.Resampling.BILINEAR)


def draw_round_cap_line(draw, pts, fill, width: float) -> None:
    """带圆头圆角的折线。"""
    w = max(1, int(round(width)))
    draw.line(pts, fill=fill, width=w, joint="curve")
    r = w / 2.0
    for x, y in (pts[0], pts[-1]):
        draw.ellipse([x - r, y - r, x + r, y + r], fill=fill)


def _apply_alpha(rgb_img: Image.Image, alpha_img: Image.Image) -> Image.Image:
    result = rgb_img.copy()
    result.putalpha(alpha_img)
    return result


def render_frame(size: int) -> Image.Image:
    """渲染某个目标尺寸的高分辨率母版，再缩小到目标尺寸。"""
    scale = 8 if size <= 64 else 4
    S = size * scale
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # ---- 贴纸本体：圆角方块 + 渐变 + 边缘描边 ----
    m = int(round(S * 0.045))
    x0 = y0 = m
    x1 = y1 = S - m - 1
    radius = int(round(S * 0.225))

    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([x0, y0, x1, y1], radius=radius, fill=255)

    img.paste(make_gradient(S), (0, 0), mask)

    # 顶部柔光高光
    sheen = Image.new("L", (1, S), 0)
    sheen.putdata([max(0, int(58 * (1.0 - y / (S * 0.42)))) for y in range(S)])
    sheen = sheen.resize((S, S))
    sheen = Image.composite(sheen, Image.new("L", (S, S), 0), mask)
    white = Image.new("RGBA", (S, S), (255, 255, 255, 255))
    img = Image.alpha_composite(img, _apply_alpha(white, sheen))

    # 边缘描边（更深一档的蓝，让图标在浅色/深色任务栏都有轮廓）
    edge_w = max(2, int(round(S * 0.011)))
    ImageDraw.Draw(img).rounded_rectangle(
        [x0, y0, x1, y1], radius=radius, outline=COLOR_EDGE, width=edge_w
    )

    # ---- 右上角折角 + 微光（小尺寸省略，避免糊成一团）----
    if size >= 32:
        c = int(round(S * 0.30))
        ax, ay = x1 - c, y0          # 折线起点（顶边）
        bx, by = x1, y0 + c          # 折线终点（右边）
        fx, fy = x1 - c, y0 + c      # 折角翻折后的内角点
        draw = ImageDraw.Draw(img)
        # 先画翻折阴影（沿折线下方），再切掉原角，最后贴翻折面
        draw.line([(ax, ay), (bx, by)], fill=COLOR_FOLD_SHADOW, width=max(2, int(round(S * 0.02))))
        draw.polygon([(ax, ay - 1), (bx + 1, ay - 1), (bx + 1, by)], fill=(0, 0, 0, 0))
        draw.polygon([(ax, ay), (bx, by), (fx, fy)], fill=COLOR_FLAP)
        draw.line([(ax, ay), (bx, by)], fill=COLOR_FLAP_EDGE, width=max(2, int(round(S * 0.008))))

    # ---- 白色对勾：阴影 + 主体 ----
    pad = x1 - x0

    def pt(fx: float, fy: float):
        return (x0 + pad * fx, y0 + pad * fy)

    pts = [pt(0.245, 0.545), pt(0.425, 0.725), pt(0.775, 0.310)]
    check_w = pad * (0.150 if size >= 32 else 0.170)

    shadow_layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    sh_pts = [(x, y + max(1.0, S * 0.010)) for x, y in pts]
    draw_round_cap_line(ImageDraw.Draw(shadow_layer), sh_pts, COLOR_CHECK_SHADOW, check_w)
    shadow_layer = shadow_layer.filter(ImageFilter.GaussianBlur(radius=max(1.0, S * 0.006)))
    img = Image.alpha_composite(img, shadow_layer)

    draw_round_cap_line(ImageDraw.Draw(img), pts, COLOR_CHECK, check_w)

    return img.resize((size, size), _resample_lanczos())


# ---------------------------------------------------------------------------
# ICO 封装（每帧单独 PNG 压缩，保留每尺寸独立渲染的清晰度）
# ---------------------------------------------------------------------------
def build_ico(frames: dict[int, Image.Image]) -> bytes:
    png_blobs = {}
    for size in SIZES:
        buf = io.BytesIO()
        frames[size].save(buf, format="PNG", optimize=True)
        png_blobs[size] = buf.getvalue()

    header = struct.pack("<HHH", 0, ICO_TYPE, len(SIZES))
    offset = 6 + 16 * len(SIZES)
    entries = []
    for size in SIZES:
        blob = png_blobs[size]
        width = 0 if size >= 256 else size
        height = 0 if size >= 256 else size
        entries.append(
            struct.pack("<BBBBHHII", width, height, 0, 0, 1, 32, len(blob), offset)
        )
        offset += len(blob)
    return header + b"".join(entries) + b"".join(png_blobs[s] for s in SIZES)


# ---------------------------------------------------------------------------
# 自检
# ---------------------------------------------------------------------------
def verify_ico(path: Path) -> bool:
    print(f"\n[自检] 校验 ICO：{path}")
    _results.clear()
    if not path.is_file():
        report(False, "文件不存在")
        return False
    data = path.read_bytes()
    report(len(data) > 4096, f"文件大小 {len(data)} 字节（>4096）")
    report(len(data) < 1024 * 1024, f"文件大小 {len(data)} 字节（<1MB，避免异常膨胀）")

    if len(data) < 6:
        report(False, "文件头不足 6 字节")
        return False
    reserved, ico_type, count = struct.unpack("<HHH", data[:6])
    report(reserved == 0, f"ICO 保留字段为 0（实际 {reserved}）")
    report(ico_type == ICO_TYPE, f"ICO 类型为 1（实际 {ico_type}）")
    report(count == len(SIZES), f"图像帧数为 {count}（期望 {len(SIZES)}）")
    if count != len(SIZES) or len(data) < 6 + 16 * count:
        return False

    entries = []
    for i, expected_size in enumerate(SIZES):
        off = 6 + 16 * i
        w, h, _colors, _res, planes, bits, nbytes, data_off = struct.unpack(
            "<BBBBHHII", data[off : off + 16]
        )
        actual_w = 256 if w == 0 else w
        actual_h = 256 if h == 0 else h
        ok = (
            actual_w == expected_size
            and actual_h == expected_size
            and planes == 1
            and bits == 32
            and data_off + nbytes <= len(data)
        )
        entries.append((expected_size, data_off, nbytes))
        report(
            ok,
            f"第 {i} 帧头：{actual_w}x{actual_h} 32bpp，偏移 {data_off}，长度 {nbytes}",
        )

    seen_sizes = []
    for expected_size, data_off, nbytes in entries:
        blob = data[data_off : data_off + nbytes]
        if not blob.startswith(PNG_SIGNATURE):
            report(False, f"{expected_size}px 帧不是合法 PNG")
            continue
        try:
            with Image.open(io.BytesIO(blob)) as im:
                im.load()
                frame = im.convert("RGBA")
        except Exception as exc:  # noqa: BLE001
            report(False, f"{expected_size}px 帧解码失败：{exc}")
            continue
        seen_sizes.append(expected_size)
        total = expected_size * expected_size
        raw = frame.tobytes()  # RGBA，每像素 4 字节
        opaque = whiteish = blueish = 0
        for i in range(0, len(raw), 4):
            r, g, b, a = raw[i], raw[i + 1], raw[i + 2], raw[i + 3]
            if a > 200:
                opaque += 1
                if r >= 215 and g >= 215 and b >= 215:
                    whiteish += 1
                if b > 120 and b >= g >= r:
                    blueish += 1
        corner_clear = (
            frame.getpixel((0, 0))[3] <= 8
            and frame.getpixel((expected_size - 1, 0))[3] <= 8
        )
        img_ok = (
            frame.size == (expected_size, expected_size)
            and opaque > total * 0.55
            and whiteish >= max(6, int(total * 0.015))
            and whiteish < total * 0.55
            and blueish > total * 0.30
            and corner_clear
        )
        report(
            img_ok,
            f"{expected_size}px 内容：不透明 {opaque}/{total}，白色对勾像素 {whiteish}，蓝色像素 {blueish}，四角透明={corner_clear}",
        )

    report(seen_sizes == SIZES, f"PNG 帧尺寸齐全且顺序正确：{seen_sizes}")
    return all(ok for ok, _ in _results)


# ---------------------------------------------------------------------------
# 预览图
# ---------------------------------------------------------------------------
def make_preview(frames: dict[int, Image.Image], path: Path) -> None:
    cell = 300
    pad = 16
    light = (242, 245, 250, 255)
    dark = (32, 38, 50, 255)
    canvas = Image.new("RGBA", (700, 780), (255, 255, 255, 255))
    ImageDraw.Draw(canvas).rectangle([pad, pad, cell + pad, cell + pad], fill=light)
    ImageDraw.Draw(canvas).rectangle(
        [cell + pad * 2, pad, cell * 2 + pad * 2, cell + pad], fill=dark
    )

    # 左：256（浅色底），右：256（深色底）
    big = frames[256]
    for bx, bg in ((pad, light), (cell + pad * 2, dark)):
        bg_img = Image.new("RGBA", (cell, cell), bg)
        bg_img.alpha_composite(big, ((cell - 256) // 2, (cell - 256) // 2))
        canvas.paste(bg_img, (bx, pad))

    # 下方：各尺寸 1x 展示（浅色一行、深色一行）
    y_light = cell + pad * 2
    y_dark = y_light + 72
    x = pad
    for size in SIZES[:-1]:
        tile_w = max(64, size + 12)
        for y, bg in ((y_light, light), (y_dark, dark)):
            tile = Image.new("RGBA", (tile_w, 64), bg)
            tile.alpha_composite(frames[size], ((tile_w - size) // 2, (64 - size) // 2))
            canvas.paste(tile, (x, y))
        x += tile_w + 6

    # 16px / 24px 放大像素图（验证小尺寸清晰度）
    zoom_y = y_dark + 80
    draw_16 = frames[16].resize((16 * 8, 16 * 8), _resample_nearest())
    draw_24 = frames[24].resize((24 * 8, 24 * 8), _resample_nearest())
    tile = Image.new("RGBA", (16 * 8 + 20, 16 * 8 + 34), light)
    tile.alpha_composite(draw_16, (10, 10))
    canvas.paste(tile, (pad, zoom_y))
    tile = Image.new("RGBA", (24 * 8 + 20, 16 * 8 + 34), dark)
    tile.alpha_composite(draw_24, (10, 10))
    canvas.paste(tile, (cell + pad * 2, zoom_y))

    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path, format="PNG")
    print(f"  [PASS] 预览图已保存：{path}")


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="生成/校验贴纸待办应用 ICO 图标")
    parser.add_argument("--output", default=str(DEFAULT_OUTPUT), help="ICO 输出路径")
    parser.add_argument(
        "--preview", default=str(DEFAULT_PREVIEW), help="预览 PNG 输出路径（传 none 则跳过）"
    )
    parser.add_argument("--check-only", action="store_true", help="只校验已有 ICO，不重新生成")
    args = parser.parse_args(argv)

    output = Path(args.output)
    print(f"[1/3] 目标尺寸：{', '.join(str(s) for s in SIZES)}")

    if not args.check_only:
        print("[2/3] 渲染各尺寸帧 ...")
        frames = {size: render_frame(size) for size in SIZES}
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_bytes(build_ico(frames))
        print(f"  [PASS] 已写入 ICO：{output}（{output.stat().st_size} 字节）")
        if args.preview.lower() != "none":
            make_preview(frames, Path(args.preview))
    else:
        print("[2/3] --check-only：跳过生成")

    print("[3/3] 校验")
    ok = verify_ico(output)
    passed = sum(1 for k, _ in _results if k)
    failed = sum(1 for k, _ in _results if not k)
    print(f"\n[自检汇总] PASS {passed} 项，FAIL {failed} 项")
    if not ok or failed:
        print("[自检结果] 失败：图标不符合交付要求", file=sys.stderr)
        return 1
    print("[自检结果] 全部通过：图标可交付")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
