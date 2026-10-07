# Generates icon.ico — stylized "futuristic floppy" in Reclaim's terminal-green
# palette. Run: py make_icon.py   (writes icon.ico next to itself)
from PIL import Image, ImageDraw, ImageFilter

S = 512  # supersample canvas, downscale at the end

NEON = (0, 230, 118, 255)     # #00e676
TEAL = (105, 240, 174, 255)   # #69f0ae
DARK = (16, 21, 16, 255)      # #101510
DARKER = (10, 14, 10, 255)    # #0a0e0a
DIM = (111, 143, 118, 255)    # #6f8f76

def body_pts(x0, y0, x1, y1, ch):
    # octagon-ish: small chamfer everywhere, bigger chamfer top-right
    return [
        (x0 + ch, y0), (x1 - 2.2 * ch, y0), (x1, y0 + 2.2 * ch),
        (x1, y1 - ch), (x1 - ch, y1), (x0 + ch, y1), (x0, y1 - ch), (x0, y0 + ch),
    ]

def floppy(dark=True):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    m = S * 0.055
    x0, y0, x1, y1 = m, m, S - m, S - m
    ch = S * 0.055
    ow = max(4, S // 90)                       # outline width

    pts = body_pts(x0, y0, x1, y1, ch)
    d.polygon(pts, fill=DARK)
    d.line(pts + [pts[0]], fill=NEON, width=ow, joint="curve")

    # metal shutter — top ~45%, shifted right, teal edge
    sx0, sx1 = S * 0.30, S * 0.84
    sy0, sy1 = y0 + ow, S * 0.46
    d.rectangle([sx0, sy0, sx1, sy1], fill=DARKER, outline=TEAL, width=ow)
    # shutter read slot
    d.line([(sx1 - (sx1 - sx0) * 0.22, sy0 + ow * 2),
            (sx1 - (sx1 - sx0) * 0.22, sy1 - ow * 2)], fill=TEAL, width=ow)

    # label area — bottom band
    lx0, ly0, lx1, ly1 = S * 0.15, S * 0.56, S * 0.85, S * 0.86
    d.rectangle([lx0, ly0, lx1, ly1], fill=DARKER, outline=DIM, width=ow)
    # label "text" lines
    lw = max(3, S // 140)
    d.line([(lx0 + 0.08 * (lx1 - lx0), ly0 + 0.30 * (ly1 - ly0)),
            (lx1 - 0.08 * (lx1 - lx0), ly0 + 0.30 * (ly1 - ly0))], fill=DIM, width=lw)
    d.line([(lx0 + 0.08 * (lx1 - lx0), ly0 + 0.62 * (ly1 - ly0)),
            (lx1 - 0.30 * (lx1 - lx0), ly0 + 0.62 * (ly1 - ly0))], fill=TEAL, width=lw)
    # accent block on the label — the "recovered data" marker
    d.rectangle([lx1 - 0.24 * (lx1 - lx0), ly0 + 0.55 * (ly1 - ly0),
                 lx1 - 0.08 * (lx1 - lx0), ly0 + 0.80 * (ly1 - ly0)], fill=NEON)

    # write-protect notch bottom-left inside body
    nx, ny = x0 + S * 0.055, y1 - S * 0.135
    d.rectangle([nx, ny, nx + S * 0.05, ny + S * 0.08], outline=TEAL, width=lw)
    return img

def glow_silhouette():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    m = S * 0.055
    d.polygon(body_pts(m, m, S - m, S - m, S * 0.055),
              fill=(0, 230, 118, 110))
    return img.filter(ImageFilter.GaussianBlur(S * 0.028))

icon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
icon.alpha_composite(glow_silhouette())
icon.alpha_composite(floppy())

out = __import__("pathlib").Path(__file__).parent / "icon.ico"
icon.save(out, format="ICO", sizes=[(256, 256), (48, 48), (32, 32), (16, 16)])
icon.resize((256, 256), Image.LANCZOS).save(out.with_suffix(".png"))
print(f"wrote {out}")
