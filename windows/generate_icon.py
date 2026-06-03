import os
from PIL import Image, ImageDraw

def render_logo(size=512, transparent=True):
    # Create image with transparent or black background
    bg_color = (0, 0, 0, 0) if transparent else (18, 18, 18, 255)
    img = Image.new("RGBA", (size, size), bg_color)
    draw = ImageDraw.Draw(img)

    # Scale factor
    f = size / 108.0

    # Draw grid if it is the dark logo
    if not transparent:
        # Subtle white lines with low opacity (equivalent to #33FFFFFF)
        grid_color = (255, 255, 255, 20)
        # Draw vertical lines
        for x in range(0, 108, 10):
            px = int(x * f)
            draw.line([(px, 0), (px, size)], fill=grid_color, width=1)
        # Draw horizontal lines
        for y in range(0, 108, 10):
            py = int(y * f)
            draw.line([(0, py), (size, py)], fill=grid_color, width=1)

    # Chevron "A" path: M54,28 L82,72 H69 L54,49 L39,72 H26 Z
    chevron_pts = [
        (54 * f, 28 * f),
        (82 * f, 72 * f),
        (69 * f, 72 * f),
        (54 * f, 49 * f),
        (39 * f, 72 * f),
        (26 * f, 72 * f)
    ]
    # YellowMain: #FFCC00 (255, 204, 0)
    draw.polygon(chevron_pts, fill=(255, 204, 0, 255))

    # Diamond path: M54,54 L61,61 L54,68 L47,61 Z
    diamond_pts = [
        (54 * f, 54 * f),
        (61 * f, 61 * f),
        (54 * f, 68 * f),
        (47 * f, 61 * f)
    ]
    # White: #FFFFFF (255, 255, 255)
    draw.polygon(diamond_pts, fill=(255, 255, 255, 255))

    return img

def main():
    out_dir = os.path.join("d:\\Сайты\\Anarise\\windows", "Resources")
    os.makedirs(out_dir, exist_ok=True)
    os.makedirs("d:\\Сайты\\Anarise\\windows\\wwwroot", exist_ok=True)

    # 1. Render high-res PNG with transparent background
    logo_trans = render_logo(512, transparent=True)
    logo_trans_path = os.path.join(out_dir, "logo_transparent.png")
    logo_trans.save(logo_trans_path, "PNG")
    print(f"Saved: {logo_trans_path}")

    # Also save to wwwroot for HTML usage
    logo_trans.save(os.path.join("d:\\Сайты\\Anarise\\windows\\wwwroot", "logo.png"), "PNG")

    # 2. Render high-res PNG with dark grid background
    logo_dark = render_logo(512, transparent=False)
    logo_dark_path = os.path.join(out_dir, "logo_dark.png")
    logo_dark.save(logo_dark_path, "PNG")
    print(f"Saved: {logo_dark_path}")

    # 3. Create Windows .ico file with multiple resolutions (transparent)
    ico_path = os.path.join(out_dir, "icon.ico")
    ico_sizes = [16, 32, 48, 64, 128, 256]
    ico_imgs = [render_logo(size, transparent=True) for size in ico_sizes]
    # Pillow allows saving list of images as an ICO file
    ico_imgs[0].save(ico_path, format="ICO", append_images=ico_imgs[1:])
    print(f"Saved: {ico_path}")

if __name__ == "__main__":
    main()
