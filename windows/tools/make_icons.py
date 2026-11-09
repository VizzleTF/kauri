"""Build Kauri icons from the generated artwork (which has a baked-in checkerboard).

usage: python tools/make_icons.py <app_icon.webp> <tray_glyph.webp>
Writes windows/Kauri/Assets/{Kauri.ico,256.png,128.png,32.png,tray.png,tray_reversed.png} and branding/kauri-icon-{256,1024}.png.
"""
import sys
from collections import deque
from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = __file__.replace('\\', '/').rsplit('/tools/', 1)[0]
ASSETS = ROOT + '/Kauri/Assets/'
BRANDING = ROOT.rsplit('/windows', 1)[0] + '/branding/'


def largest_component(mask):
    """Keep the largest 8-connected True region of an L-mode mask (drops watermark sparkles)."""
    w, h = mask.size
    px = mask.load()
    seen = bytearray(w * h)
    best = []
    for y0 in range(h):
        for x0 in range(w):
            if px[x0, y0] < 128 or seen[y0 * w + x0]:
                continue
            comp, q = [], deque([(x0, y0)])
            seen[y0 * w + x0] = 1
            while q:
                x, y = q.popleft()
                comp.append((x, y))
                for dx in (-1, 0, 1):
                    for dy in (-1, 0, 1):
                        nx, ny = x + dx, y + dy
                        if 0 <= nx < w and 0 <= ny < h and not seen[ny * w + nx] and px[nx, ny] >= 128:
                            seen[ny * w + nx] = 1
                            q.append((nx, ny))
            if len(comp) > len(best):
                best = comp
    out = Image.new('L', mask.size, 0)
    op = out.load()
    for x, y in best:
        op[x, y] = 255
    return out


def square(im, margin=0.04):
    bbox = im.getbbox()
    im = im.crop(bbox)
    side = int(max(im.size) * (1 + 2 * margin))
    canvas = Image.new('RGBA', (side, side), (0, 0, 0, 0))
    canvas.alpha_composite(im, ((side - im.width) // 2, (side - im.height) // 2))
    return canvas


def app_icon(path):
    src = Image.open(path).convert('RGB')
    w, h = src.size
    px = src.load()

    def is_bg(c):
        r, g, b = c
        return max(r, g, b) - min(r, g, b) <= 10 and 110 <= (r + g + b) / 3 <= 200

    # Flood the checkerboard from the border.
    bg = Image.new('L', (w, h), 0)
    bp = bg.load()
    q = deque([(x, y) for x in range(w) for y in (0, h - 1)] + [(x, y) for y in range(h) for x in (0, w - 1)])
    while q:
        x, y = q.popleft()
        if bp[x, y] or not is_bg(px[x, y]):
            continue
        bp[x, y] = 255
        for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
            if 0 <= nx < w and 0 <= ny < h and not bp[nx, ny]:
                q.append((nx, ny))
    fg = ImageChops.invert(bg)
    fg = largest_component(fg)
    # Shave the 1px halo of blended checkerboard and soften the edge.
    fg = fg.filter(ImageFilter.MinFilter(3)).filter(ImageFilter.GaussianBlur(0.8))
    rgba = src.convert('RGBA')
    rgba.putalpha(fg)
    return square(rgba)


def tray_glyph(path):
    src = Image.open(path).convert('L')
    # Black glyph on a white/light-grey checkerboard: darkness becomes alpha.
    soft = src.point(lambda v: 0 if v >= 200 else 255 if v <= 40 else int((200 - v) * 255 / 160))
    hard = soft.point(lambda v: 255 if v > 127 else 0)
    glyph = Image.new('RGBA', src.size, (0, 0, 0, 255))
    glyph.putalpha(soft)
    box = hard.getbbox()

    # Reversed variant: the board filled solid, with every inner shape (tree, clip) knocked out.
    # Close the gaps in the frame next to the clip before filling the board.
    closed = hard.filter(ImageFilter.MaxFilter(41)).filter(ImageFilter.MinFilter(41))
    filled = closed.copy()
    ImageDraw.floodfill(filled, (0, 0), 128)
    board = filled.point(lambda v: 0 if v == 128 else 255)
    outline = largest_component(hard)
    inner = ImageChops.subtract(hard, outline)
    rev_a = ImageChops.subtract(board, inner).filter(ImageFilter.GaussianBlur(0.8))
    rev = Image.new('RGBA', src.size, (0, 0, 0, 255))
    rev.putalpha(rev_a)
    return square(glyph.crop(box).copy(), 0.02), square(rev.crop(box).copy(), 0.02)


def main():
    app, tray = sys.argv[1], sys.argv[2]
    icon = app_icon(app)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    imgs = {s: icon.resize((s, s), Image.LANCZOS) for s in sizes}
    imgs[256].save(ASSETS + 'Kauri.ico', sizes=[(s, s) for s in sizes], append_images=[imgs[s] for s in sizes[:-1]])
    for s in (256, 128, 32):
        imgs[s].save(ASSETS + f'{s}.png')
    imgs[256].save(BRANDING + 'kauri-icon-256.png')
    icon.resize((1024, 1024), Image.LANCZOS).save(BRANDING + 'kauri-icon-1024.png')

    glyph, rev = tray_glyph(tray)
    glyph.resize((64, 64), Image.LANCZOS).save(ASSETS + 'tray.png')
    rev.resize((64, 64), Image.LANCZOS).save(ASSETS + 'tray_reversed.png')
    print('ok', icon.size, glyph.size)


if __name__ == '__main__':
    main()
