"""
Generate CommitBall icon: red-blue gradient ball with white outline and infinity symbol.
Outputs commitball.ico with multiple sizes (16, 32, 48, 256).
Requires: pip install Pillow numpy
"""

import numpy as np
from PIL import Image, ImageDraw, ImageFont
import os

SIZE = 256
CENTER = SIZE / 2
RADIUS = 108
OUTLINE_WIDTH = 6

def make_icon():
    # Create coordinate grids
    y_grid, x_grid = np.mgrid[0:SIZE, 0:SIZE].astype(np.float64)
    dx = x_grid - CENTER
    dy = y_grid - CENTER
    dist = np.sqrt(dx * dx + dy * dy)

    # Create circular mask with anti-aliasing
    mask = np.clip(RADIUS - dist + 0.5, 0, 1)

    # Create outline mask
    outline_mask = np.clip(dist - (RADIUS - OUTLINE_WIDTH) + 0.5, 0, 1) * \
                   np.clip(RADIUS - dist + 0.5, 0, 1)

    # Gradient: blue (#3B82F6) top-left to red (#EF4444) bottom-right
    blue = np.array([59, 130, 246], dtype=np.float64)
    red = np.array([239, 68, 68], dtype=np.float64)
    t = np.clip((x_grid + y_grid) / (2 * SIZE), 0, 1)

    r = (blue[0] + (red[0] - blue[0]) * t).astype(np.uint8)
    g = (blue[1] + (red[1] - blue[1]) * t).astype(np.uint8)
    b = (blue[2] + (red[2] - blue[2]) * t).astype(np.uint8)

    # Build RGBA image
    img = np.zeros((SIZE, SIZE, 4), dtype=np.uint8)
    img[:, :, 0] = r
    img[:, :, 1] = g
    img[:, :, 2] = b
    img[:, :, 3] = (mask * 255).astype(np.uint8)

    # Apply white outline
    outline_bool = outline_mask > 0.1
    img[outline_bool, 0] = 255
    img[outline_bool, 1] = 255
    img[outline_bool, 2] = 255
    img[outline_bool, 3] = (outline_mask[outline_bool] * 255).astype(np.uint8)

    pil_img = Image.fromarray(img, "RGBA")

    # Draw infinity symbol
    draw = ImageDraw.Draw(pil_img)
    font = None
    font_size = 150
    for fp in [
        "C:/Windows/Fonts/segoeui.ttf",
        "C:/Windows/Fonts/arial.ttf",
        "C:/Windows/Fonts/calibri.ttf",
    ]:
        if os.path.exists(fp):
            try:
                font = ImageFont.truetype(fp, font_size)
                break
            except Exception:
                continue
    if font is None:
        font = ImageFont.load_default()

    text = "\u221E"
    bbox = draw.textbbox((0, 0), text, font=font)
    tw = bbox[2] - bbox[0]
    th = bbox[3] - bbox[1]
    tx = int(CENTER - tw / 2 - bbox[0])
    ty = int(CENTER - th / 2 - bbox[1]) + 8

    # White text
    draw.text((tx, ty), text, fill=(255, 255, 255, 255), font=font)

    # Save PNG preview
    dir_path = os.path.dirname(os.path.abspath(__file__))
    png_path = os.path.join(dir_path, "commitball.png")
    pil_img.save(png_path)
    print(f"Preview: {png_path}")

    # Save ICO with multiple sizes (Pillow resizes from source)
    ico_path = os.path.join(dir_path, "commitball.ico")
    pil_img.save(
        ico_path,
        format="ICO",
        sizes=[(16, 16), (32, 32), (48, 48), (256, 256)],
    )
    print(f"Icon: {ico_path} ({os.path.getsize(ico_path)} bytes)")

if __name__ == "__main__":
    make_icon()
