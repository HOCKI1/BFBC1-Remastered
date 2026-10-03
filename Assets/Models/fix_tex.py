import os
import argparse

IMAGE_EXTENSIONS = {
    '.png', '.jpg', '.jpeg', '.tga', '.dds', '.bmp', 
    '.webp', '.tif', '.tiff', '.psd', '.exr', '.hdr'
}

def hide_zero_byte_images(target_path):
    if not os.path.exists(target_path):
        print(f"[DEBUG] Error: Target path '{target_path}' does not exist.")
        return

    print(f"[DEBUG] Starting scan for 0-byte images in: {target_path}\n")

    total_checked = 0
    renamed_count = 0

    for root, _, files in os.walk(target_path):
        for file in files:
            ext = os.path.splitext(file)[1].lower()
            if ext in IMAGE_EXTENSIONS:
                total_checked += 1
                file_path = os.path.join(root, file)

                try:
                    # Check if file size is 0 bytes
                    if os.path.getsize(file_path) == 0:
                        # Skip if it already starts with a dot
                        if file.startswith('.'):
                            continue

                        new_filename = f".{file}"
                        new_file_path = os.path.join(root, new_filename)

                        os.rename(file_path, new_file_path)
                        renamed_count += 1
                        print(f"[DEBUG] HIDDEN (0-byte): '{file}' -> '{new_filename}'")
                        print(f"[DEBUG] PATH: {file_path}")

                except Exception as e:
                    print(f"[DEBUG] ERROR processing file '{file_path}': {e}")

    print("\n[DEBUG] === PROCESS SUMMARY ===")
    print(f"[DEBUG] Total images checked: {total_checked}")
    print(f"[DEBUG] 0-byte images hidden with '.': {renamed_count}")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Rename 0-byte images with a leading dot for Unity ignore.")
    parser.add_argument("path", type=str, help="Path to the target directory")

    args = parser.parse_args()
    hide_zero_byte_images(args.path)