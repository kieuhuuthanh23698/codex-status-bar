from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parent
SCALE = 4
SIZE = 256
LARGE = SIZE * SCALE


def blend(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def create_icon(dot_color):
    start = (13, 18, 36)
    end = (32, 38, 74)
    gradient = Image.new("RGBA", (SIZE, SIZE))
    pixels = gradient.load()
    for y in range(SIZE):
        for x in range(SIZE):
            pixels[x, y] = (*blend(start, end, (x + y) / (2 * (SIZE - 1))), 255)
    image = gradient.resize((LARGE, LARGE), Image.Resampling.LANCZOS)
    mask = Image.new("L", (LARGE, LARGE), 0)
    draw_mask = ImageDraw.Draw(mask)
    draw_mask.rounded_rectangle((8*SCALE, 8*SCALE, 248*SCALE, 248*SCALE), radius=58*SCALE, fill=255)
    image.putalpha(mask)

    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((9*SCALE, 9*SCALE, 247*SCALE, 247*SCALE), radius=57*SCALE,
                           outline=(69, 75, 117, 255), width=3*SCALE)

    arc_box = (56*SCALE, 56*SCALE, 200*SCALE, 200*SCALE)
    stroke = 20*SCALE
    glyph = (205, 196, 255, 255)
    draw.arc(arc_box, start=50, end=310, fill=glyph, width=stroke)
    # Round the open ends of the C so it stays clean at tray-icon sizes.
    for angle in (50, 310):
        import math
        radians = math.radians(angle)
        cx = (128 + 72 * math.cos(radians)) * SCALE
        cy = (128 + 72 * math.sin(radians)) * SCALE
        radius = stroke / 2
        draw.ellipse((cx-radius, cy-radius, cx+radius, cy+radius), fill=glyph)

    draw.rounded_rectangle((105*SCALE, 106*SCALE, 166*SCALE, 118*SCALE), radius=6*SCALE,
                           fill=(243, 240, 255, 255))
    draw.rounded_rectangle((94*SCALE, 122*SCALE, 166*SCALE, 134*SCALE), radius=6*SCALE,
                           fill=(125, 211, 252, 255))
    draw.rounded_rectangle((112*SCALE, 138*SCALE, 166*SCALE, 150*SCALE), radius=6*SCALE,
                           fill=(196, 181, 253, 255))

    dot_box = (181*SCALE, 49*SCALE, 203*SCALE, 71*SCALE)
    draw.ellipse(dot_box, fill=(236, 253, 245, 255))
    inset = 4*SCALE
    draw.ellipse((dot_box[0]+inset, dot_box[1]+inset, dot_box[2]-inset, dot_box[3]-inset),
                 fill=dot_color)
    return image


variants = {
    "CodexStatusBar.ico": (52, 211, 153, 255),
    "CodexStatusBar.Warning.ico": (251, 191, 36, 255),
    "CodexStatusBar.Error.ico": (248, 113, 113, 255),
}

for filename, color in variants.items():
    icon = create_icon(color)
    if filename == "CodexStatusBar.ico":
        icon.resize((SIZE, SIZE), Image.Resampling.LANCZOS).save(ROOT / "CodexStatusBar-preview.png")
    icon.save(ROOT / filename, format="ICO", sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
