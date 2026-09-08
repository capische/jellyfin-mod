#!/usr/bin/env python3
"""Package an existing Release build with JPRM metadata and its installed-card image."""

import argparse
import json
from pathlib import Path
import shutil

import jprm


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary_dir", type=Path)
    parser.add_argument("output_dir", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    config = jprm.get_config(str(root))
    image = root / config["image"]
    artifacts = [(args.binary_dir / name, name) for name in config["artifacts"]]
    artifacts.append((image, image.name))
    for source, _ in artifacts:
        if not source.is_file() or not source.stat().st_size:
            parser.error(f"Missing or empty package artifact: {source}")

    # JPRM 1.1 emits the repository's legacy `image` field. Jellyfin 10.11's
    # installed-plugin image endpoint instead reads `imagePath` from meta.json.
    config["image"] = image.name
    metadata = jprm.generate_metadata(config)
    metadata["imagePath"] = image.name
    metadata["assemblies"] = [name for name in config["artifacts"] if name.endswith(".dll")]
    metadata["autoUpdate"] = False

    args.output_dir.mkdir(parents=True, exist_ok=True)
    for source, name in artifacts:
        destination = args.output_dir / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, destination)
    (args.output_dir / "meta.json").write_text(json.dumps(metadata, indent=4) + "\n")
    print(f"Packaged {metadata['name']} {metadata['version']} in {args.output_dir}")


if __name__ == "__main__":
    main()
